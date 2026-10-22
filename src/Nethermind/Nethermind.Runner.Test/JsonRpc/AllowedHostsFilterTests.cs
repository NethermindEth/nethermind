// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Nethermind.Config;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.JsonRpc;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class AllowedHostsFilterTests
{
    private const int RpcPort = 8545;
    private const int EnginePort = 8551;

    private static readonly StringSegment[] Patterns = ["localhost", "*.example.com"];

    [TestCase("localhost", ExpectedResult = true)]
    [TestCase("localhost:8545", ExpectedResult = true, TestName = "Ignores the port")]
    [TestCase("LocalHost", ExpectedResult = true, TestName = "Ignores the case")]
    [TestCase("node.example.com:8551", ExpectedResult = true, TestName = "Matches a wildcard subdomain")]
    [TestCase("example.com", ExpectedResult = false, TestName = "Wildcard does not match the parent domain")]
    [TestCase("localhost.evil.com", ExpectedResult = false)]
    [TestCase("evil.com", ExpectedResult = false)]
    [TestCase("127.0.0.1:8545", ExpectedResult = true, TestName = "Accepts an IPv4 literal")]
    [TestCase("[::1]:8545", ExpectedResult = true, TestName = "Accepts an IPv6 literal")]
    [TestCase("", ExpectedResult = true, TestName = "Accepts a missing Host header")]
    public bool IsAllowed_matches_host_patterns(string host) =>
        AllowedHostsFilter.IsAllowed(new HostString(host), Patterns);

    [TestCase(RpcPort, "rpc.example.com", StatusCodes.Status204NoContent)]
    [TestCase(RpcPort, "engine.example.com", StatusCodes.Status403Forbidden)]
    [TestCase(EnginePort, "engine.example.com", StatusCodes.Status204NoContent)]
    [TestCase(EnginePort, "rpc.example.com", StatusCodes.Status403Forbidden)]
    public async Task Application_applies_allowed_hosts_per_port_before_startup_filters(int port, string host, int expectedStatusCode)
    {
        JsonRpcConfig config = CreateConfig();
        config.AllowedHosts = ["rpc.example.com"];
        config.EngineAllowedHosts = ["engine.example.com"];

        Assert.That(await SendAsync(config, port, host), Is.EqualTo(expectedStatusCode));
    }

    [Test]
    public async Task Application_accepts_any_host_by_default([Values(RpcPort, EnginePort)] int port) =>
        Assert.That(await SendAsync(CreateConfig(), port, "evil.com"), Is.EqualTo(StatusCodes.Status204NoContent));

    private static JsonRpcConfig CreateConfig() => new() { Enabled = true, Port = RpcPort, EnginePort = EnginePort };

    /// <summary>
    /// Builds the request pipeline as <see cref="WebHost"/> does, with a startup filter that answers every request,
    /// as plugin middleware such as SSZ-REST does for its routes.
    /// </summary>
    private static async Task<int> SendAsync(JsonRpcConfig config, int port, string host)
    {
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetConfig<IJsonRpcConfig>().Returns(config);
        IServer server = Substitute.For<IServer>();
        server.Features.Returns(new FeatureCollection());

        ServiceCollection services = new();
        services.AddSingleton(configProvider);
        services.AddSingleton<IJsonRpcUrlCollection>(new JsonRpcUrlCollection(LimboLogs.Instance, config, includeWebSockets: false));
        services.AddSingleton(server);
        services.AddSingleton<IApplicationBuilderFactory, ApplicationBuilderFactory>();
        services.AddSingleton<IStartupFilter, AnsweringStartupFilter>();
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        WebHost webHost = new(serviceProvider, new ConfigurationBuilder().Build(), Substitute.For<IStartup>(), LimboLogs.Instance);
        RequestDelegate application = webHost.BuildApplication();

        DefaultHttpContext context = new()
        {
            Connection = { LocalPort = port },
            Request = { Host = new HostString(host) }
        };
        await application(context);
        return context.Response.StatusCode;
    }

    private sealed class AnsweringStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Run(static ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
            next(app);
        };
    }
}
