// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Mcp;
using Nethermind.Mcp.Hosting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Test.Hosting;

[TestFixture]
public class McpWebHostTests
{
    private static IServiceProvider BuildMcpServices()
    {
        ContainerBuilder builder = new();
        builder.RegisterInstance(Substitute.For<INethermindApi>()).As<INethermindApi>();
        builder.RegisterInstance(Substitute.For<IConfigProvider>()).As<IConfigProvider>();
        builder.RegisterModule(new McpServerPluginModule());
        IContainer container = builder.Build();
        return new AutofacResolverServiceProvider(container);
    }

    private static McpConfig EphemeralConfig(string? apiKey = null) => new()
    {
        Enabled = true,
        HttpEnabled = true,
        HttpHost = "127.0.0.1",
        HttpPort = 0,
        ApiKey = apiKey,
        MaxConcurrent = 4,
    };

    [Test]
    public async Task Start_binds_to_configured_port_and_serves_mcp_endpoint()
    {
        await using McpWebHost host = new(EphemeralConfig(), LimboLogs.Instance, BuildMcpServices());
        bool started = await host.StartAsync(CancellationToken.None);

        Assert.That(started, Is.True);
        Assert.That(host.BoundUri, Is.Not.Null);

        using HttpClient client = new();
        client.BaseAddress = host.BoundUri;
        using HttpResponseMessage response = await client.GetAsync("/mcp");

        Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.NotFound),
            $"Expected /mcp to be mounted; got {(int)response.StatusCode} {response.StatusCode}");

        await host.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Start_returns_false_when_port_in_use()
    {
        await using McpWebHost first = new(EphemeralConfig(), LimboLogs.Instance, BuildMcpServices());
        bool firstStarted = await first.StartAsync(CancellationToken.None);
        Assert.That(firstStarted, Is.True);
        Assert.That(first.BoundUri, Is.Not.Null);

        int boundPort = first.BoundUri!.Port;

        McpConfig collidingConfig = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = "127.0.0.1",
            HttpPort = boundPort,
            ApiKey = null,
            MaxConcurrent = 4,
        };

        await using McpWebHost second = new(collidingConfig, LimboLogs.Instance, BuildMcpServices());
        bool secondStarted = await second.StartAsync(CancellationToken.None);

        Assert.That(secondStarted, Is.False, "second host should fail to bind same port");
        Assert.That(second.BoundUri, Is.Null, "BoundUri must remain null after failed start");

        await first.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Stop_releases_the_port()
    {
        await using McpWebHost host = new(EphemeralConfig(), LimboLogs.Instance, BuildMcpServices());
        bool started = await host.StartAsync(CancellationToken.None);
        Assert.That(started, Is.True);
        int boundPort = host.BoundUri!.Port;

        await host.StopAsync(CancellationToken.None);

        // Re-start on the previously bound port to prove it was released.
        McpConfig sameConfig = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = "127.0.0.1",
            HttpPort = boundPort,
            ApiKey = null,
            MaxConcurrent = 4,
        };

        await using McpWebHost reborn = new(sameConfig, LimboLogs.Instance, BuildMcpServices());
        bool restarted = await reborn.StartAsync(CancellationToken.None);

        Assert.That(restarted, Is.True, "second start on same port should succeed once first released it");

        await reborn.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task BoundUri_reflects_actual_port_after_ephemeral_bind()
    {
        await using McpWebHost host = new(EphemeralConfig(), LimboLogs.Instance, BuildMcpServices());
        bool started = await host.StartAsync(CancellationToken.None);
        Assert.That(started, Is.True);

        Assert.That(host.BoundUri, Is.Not.Null);
        Assert.That(host.BoundUri!.Port, Is.GreaterThan(0));

        await host.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task When_apiKey_is_set_request_without_bearer_returns_401()
    {
        await using McpWebHost host = new(EphemeralConfig(apiKey: "secret"), LimboLogs.Instance, BuildMcpServices());
        bool started = await host.StartAsync(CancellationToken.None);
        Assert.That(started, Is.True);

        using HttpClient client = new();
        client.BaseAddress = host.BoundUri;
        using HttpResponseMessage response = await client.GetAsync("/mcp");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        await host.StopAsync(CancellationToken.None);
    }

    [TestCase("localhost", "127.0.0.1")]
    [TestCase("LOCALHOST", "127.0.0.1")]
    [TestCase("127.0.0.1", "127.0.0.1")]
    [TestCase("0.0.0.0", "0.0.0.0")]
    [TestCase("::", "::")]
    [TestCase("[::]", "::")]
    [TestCase("::1", "::1")]
    [TestCase("[::1]", "::1")]
    [TestCase("192.168.1.5", "192.168.1.5")]
    public void ResolveBindAddress_maps_well_known_names(string input, string expected)
    {
        IPAddress address = McpWebHost.ResolveBindAddress(input);
        Assert.That(address, Is.EqualTo(IPAddress.Parse(expected)));
    }

    [Test]
    public void ResolveBindAddress_throws_FormatException_for_arbitrary_hostname() =>
        Assert.Throws<FormatException>(() => McpWebHost.ResolveBindAddress("mynode.local"));

    [Test]
    public async Task Start_with_localhost_binds_to_loopback()
    {
        McpConfig config = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = "localhost",
            HttpPort = 0,
            ApiKey = null,
            MaxConcurrent = 4,
        };
        await using McpWebHost host = new(config, LimboLogs.Instance, BuildMcpServices());

        bool started = await host.StartAsync(CancellationToken.None);

        Assert.That(started, Is.True);
        Assert.That(host.BoundUri, Is.Not.Null);
        Assert.That(host.BoundUri!.Host, Is.EqualTo("127.0.0.1"));

        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Bridges an Autofac container to <see cref="IServiceProvider"/> for tests.
    /// </summary>
    private sealed class AutofacResolverServiceProvider(IContainer container) : IServiceProvider, IDisposable
    {
        public object? GetService(Type serviceType) => container.ResolveOptional(serviceType);

        public void Dispose() => container.Dispose();
    }
}
