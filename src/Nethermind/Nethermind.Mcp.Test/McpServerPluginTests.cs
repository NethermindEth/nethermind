// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Mcp.Hosting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Test;

[TestFixture]
public class McpServerPluginTests
{
    [Test]
    public void Plugin_is_disabled_by_default()
    {
        // A fresh config has Enabled = false; before Init runs, the plugin reports disabled.
        McpServerPlugin plugin = new();

        Assert.That(plugin.Enabled, Is.False);
    }

    [Test]
    public async Task Init_logs_warning_when_host_is_non_loopback_without_apiKey()
    {
        McpConfig config = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = "0.0.0.0",
            HttpPort = 8550,
            ApiKey = null,
        };

        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        interfaceLogger.IsWarn.Returns(true);
        INethermindApi api = BuildApi(config, interfaceLogger);

        McpServerPlugin plugin = new();
        await plugin.Init(api);

        interfaceLogger.Received(1).Warn(Arg.Is<string>(s => s.Contains("MCP exposed without authentication")));
    }

    [TestCase("127.0.0.1", null)]
    [TestCase("0.0.0.0", "secret")]
    [TestCase("localhost", null)]
    [TestCase("::1", null)]
    public async Task Init_does_not_warn_when_loopback_or_apiKey_set(string host, string? apiKey)
    {
        McpConfig config = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = host,
            HttpPort = 8550,
            ApiKey = apiKey,
        };

        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        interfaceLogger.IsWarn.Returns(true);
        INethermindApi api = BuildApi(config, interfaceLogger);

        McpServerPlugin plugin = new();
        await plugin.Init(api);

        interfaceLogger.DidNotReceive().Warn(Arg.Is<string>(s => s.Contains("MCP exposed without authentication")));
    }

    [Test]
    public async Task InitRpcModules_starts_WebHost_when_enabled()
    {
        McpConfig config = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = "127.0.0.1",
            HttpPort = 0,
        };

        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        McpWebHost host = Substitute.For<McpWebHost>();
        host.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        INethermindApi api = BuildApi(config, interfaceLogger, host);

        McpServerPlugin plugin = new();
        await plugin.Init(api);
        await plugin.InitRpcModules();

        await host.Received(1).StartAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InitRpcModules_does_not_throw_when_WebHost_start_fails()
    {
        McpConfig config = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = "127.0.0.1",
            HttpPort = 0,
        };

        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        interfaceLogger.IsError.Returns(true);
        McpWebHost host = Substitute.For<McpWebHost>();
        host.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        INethermindApi api = BuildApi(config, interfaceLogger, host);

        McpServerPlugin plugin = new();
        await plugin.Init(api);
        Assert.DoesNotThrowAsync(async () => await plugin.InitRpcModules());

        interfaceLogger.Received(1).Error(Arg.Any<string>(), Arg.Any<System.Exception?>());
    }

    [Test]
    public async Task InitRpcModules_skips_when_disabled()
    {
        McpConfig config = new()
        {
            Enabled = false,
            HttpEnabled = true,
            HttpHost = "127.0.0.1",
            HttpPort = 0,
        };

        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        McpWebHost host = Substitute.For<McpWebHost>();
        INethermindApi api = BuildApi(config, interfaceLogger, host);

        McpServerPlugin plugin = new();
        await plugin.Init(api);
        await plugin.InitRpcModules();

        await host.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InitRpcModules_skips_when_http_disabled()
    {
        McpConfig config = new()
        {
            Enabled = true,
            HttpEnabled = false,
            HttpHost = "127.0.0.1",
            HttpPort = 0,
        };

        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        McpWebHost host = Substitute.For<McpWebHost>();
        INethermindApi api = BuildApi(config, interfaceLogger, host);

        McpServerPlugin plugin = new();
        await plugin.Init(api);
        await plugin.InitRpcModules();

        await host.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DisposeAsync_stops_WebHost()
    {
        McpConfig config = new()
        {
            Enabled = true,
            HttpEnabled = true,
            HttpHost = "127.0.0.1",
            HttpPort = 0,
        };

        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        McpWebHost host = Substitute.For<McpWebHost>();
        host.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        host.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        host.DisposeAsync().Returns(default(System.Threading.Tasks.ValueTask));
        INethermindApi api = BuildApi(config, interfaceLogger, host);

        McpServerPlugin plugin = new();
        await plugin.Init(api);
        await plugin.InitRpcModules();

        await plugin.DisposeAsync();

        Received.InOrder(() =>
        {
            host.StopAsync(Arg.Any<CancellationToken>());
            host.DisposeAsync();
        });
    }

    [Test]
    public async Task DisposeAsync_is_a_noop_when_host_was_never_started()
    {
        McpConfig config = new()
        {
            Enabled = false,
        };

        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        McpWebHost host = Substitute.For<McpWebHost>();
        INethermindApi api = BuildApi(config, interfaceLogger, host);

        McpServerPlugin plugin = new();
        await plugin.Init(api);

        await plugin.DisposeAsync();

        await host.DidNotReceive().StopAsync(Arg.Any<CancellationToken>());
        await host.DidNotReceive().DisposeAsync();
    }

    [Test]
    public void Module_returns_McpServerPluginModule()
    {
        McpServerPlugin plugin = new();

        Assert.That(plugin.Module, Is.InstanceOf<McpServerPluginModule>());
    }

    [Test]
    public void Plugin_metadata_is_set()
    {
        McpServerPlugin plugin = new();

        Assert.That(plugin.Name, Is.EqualTo("Mcp"));
        Assert.That(plugin.Description, Is.EqualTo("Model Context Protocol server"));
        Assert.That(plugin.Author, Is.EqualTo("Nethermind"));
        Assert.That(plugin.MustInitialize, Is.False);
    }

    private static INethermindApi BuildApi(IMcpConfig config, InterfaceLogger interfaceLogger, McpWebHost? host = null)
    {
        INethermindApi api = Substitute.For<INethermindApi>();

        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetConfig<IMcpConfig>().Returns(config);
        api.ConfigProvider.Returns(configProvider);
        // NSubstitute also intercepts the default-interface-method `Config<T>()`, so wire it explicitly.
        api.Config<IMcpConfig>().Returns(config);

        ILogManager logManager = Substitute.For<ILogManager>();
        ILogger logger = new(interfaceLogger);
        logManager.GetClassLogger<McpServerPlugin>().Returns(logger);
        api.LogManager.Returns(logManager);

        ContainerBuilder builder = new();
        if (host is not null)
        {
            builder.RegisterInstance(host).As<McpWebHost>();
        }
        IContainer container = builder.Build();
        api.Context.Returns(container);

        return api;
    }
}
