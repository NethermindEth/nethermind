// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Net;
using System.Net.Sockets;
using Autofac;
using ModelContextProtocol.Client;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class McpPluginTests
{
    [Test]
    public void Plugin_is_disabled_by_default()
    {
        INethermindPlugin plugin = new McpPlugin(new McpConfig());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plugin.Enabled, Is.False);
            Assert.That(plugin.Name, Is.EqualTo("Mcp"));
            Assert.That(plugin.MustInitialize, Is.False, "a disabled optional plugin must never block startup");
            Assert.That(plugin.Module, Is.InstanceOf<McpModule>());
        }
    }

    [TestCase(null, false)]
    [TestCase("false", false)]
    [TestCase("true", true)]
    public async Task Plugin_loader_discovers_plugin_and_honours_Mcp_Enabled(string? enabled, bool expectedLoaded)
    {
        ConfigProvider configProvider = new();
        Dictionary<string, string> args = [];
        if (enabled is not null) args["Mcp.Enabled"] = enabled;
        configProvider.AddSource(new ArgsConfigSource(args));
        configProvider.Initialize();

        PluginLoader loader = new(string.Empty, Substitute.For<IFileSystem>(), NullLogger.Instance, typeof(McpPlugin));
        loader.Load();
        IList<INethermindPlugin> plugins = await loader.LoadPlugins(configProvider, new ChainSpec());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loader.PluginTypes, Does.Contain(typeof(McpPlugin)));
            Assert.That(plugins.OfType<McpPlugin>().Any(), Is.EqualTo(expectedLoaded));
        }
    }

    [Test]
    public void Startup_step_runs_after_rpc_modules_and_must_initialize()
    {
        StepInfo step = new(typeof(StartMcpServer));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(step.Dependencies, Does.Contain(typeof(RegisterRpcModules)),
                "tools rent eth modules, so the server must start after they are registered");
            Assert.That(typeof(IStep).IsAssignableFrom(typeof(StartMcpServer)), Is.True);
        }
    }

    [Test]
    public async Task Module_wires_host_and_startup_step_into_the_node_container()
    {
        await using McpTestNode node = await McpTestNode.Create(start: false);
        IContainer container = node.Chain.Container;

        StepInfo[] steps = container.Resolve<IEnumerable<StepInfo>>().Where(static s => s.StepType == typeof(StartMcpServer)).ToArray();
        StartMcpServer startStep = container.Resolve<StartMcpServer>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(steps, Has.Length.EqualTo(1), "the startup step must be registered exactly once");
            Assert.That(startStep.MustInitialize, Is.True, "an enabled server that cannot start must abort node startup");
            Assert.That(container.Resolve<McpHost>(), Is.SameAs(node.Host), "the host must be a singleton");
            Assert.That(node.Host.Endpoint, Is.Null, "nothing listens before the step runs");
        }
    }

    [Test]
    public async Task Startup_step_starts_the_listener()
    {
        await using McpTestNode node = await McpTestNode.Create(start: false);

        await node.Chain.Container.Resolve<StartMcpServer>().Execute(CancellationToken.None);

        Assert.That(node.Host.Endpoint, Is.Not.Null);
        await using McpClient client = await node.CreateClient();
        Assert.That(await client.ListToolsAsync(), Has.Count.EqualTo(McpAssert.ToolNames.Length));
    }

    private static IEnumerable<TestCaseData> InvalidStartupConfigs()
    {
        yield return new TestCaseData((Action<McpConfig>)(c => c.Host = "0.0.0.0")).SetName("Start_fails_on_wildcard_host");
        yield return new TestCaseData((Action<McpConfig>)(c => c.Host = "192.168.1.10")).SetName("Start_fails_on_lan_host");
        yield return new TestCaseData((Action<McpConfig>)(c => c.MaxConcurrentToolCalls = 0)).SetName("Start_fails_on_zero_concurrency");
        yield return new TestCaseData((Action<McpConfig>)(c => c.ToolTimeout = int.MaxValue)).SetName("Start_fails_on_tool_timeout_above_rpc_timeout");
        yield return new TestCaseData((Action<McpConfig>)(c => c.AuthTokenFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))).SetName("Start_fails_on_missing_token_file");
    }

    [TestCaseSource(nameof(InvalidStartupConfigs))]
    public async Task Startup_step_fails_on_invalid_config(Action<McpConfig> configure)
    {
        await using McpTestNode node = await McpTestNode.Create(configure, start: false);

        Assert.ThrowsAsync<InvalidConfigurationException>(() => node.Chain.Container.Resolve<StartMcpServer>().Execute(CancellationToken.None));
        Assert.That(node.Host.Endpoint, Is.Null, "nothing may listen after a failed start");
    }

    [Test]
    public async Task Start_fails_when_port_is_taken()
    {
        TcpListener squatter = new(IPAddress.Loopback, 0);
        squatter.Start();
        try
        {
            int port = ((IPEndPoint)squatter.LocalEndpoint).Port;
            await using McpTestNode node = await McpTestNode.Create(c => c.Port = port, start: false);

            InvalidOperationException exception = Assert.ThrowsAsync<InvalidOperationException>(() => node.Host.StartAsync(CancellationToken.None))!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.Message, Does.Contain(port.ToString()), "the error must name the port");
                Assert.That(node.Host.Endpoint, Is.Null);
            }
        }
        finally
        {
            squatter.Stop();
        }
    }

    [Test]
    public async Task Cancelled_start_leaves_no_listener()
    {
        await using McpTestNode node = await McpTestNode.Create(start: false);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Assert.CatchAsync<OperationCanceledException>(() => node.Host.StartAsync(cts.Token));
        Assert.That(node.Host.Endpoint, Is.Null);
    }

    [Test]
    public async Task Start_twice_throws()
    {
        await using McpTestNode node = await McpTestNode.Create();

        Assert.ThrowsAsync<InvalidOperationException>(() => node.Host.StartAsync(CancellationToken.None));
        Assert.That(node.Host.Endpoint, Is.Not.Null, "the running listener must survive the rejected second start");
    }

    [Test]
    public async Task Dispose_releases_the_port_and_is_idempotent()
    {
        await using McpTestNode node = await McpTestNode.Create();
        McpHost host = node.Host;
        int port = node.Endpoint.Port;

        await host.DisposeAsync();
        Assert.That(async () => await host.DisposeAsync(), Throws.Nothing, "a second dispose must be a no-op");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(host.Endpoint, Is.Null);
            Assert.That(() => BindAndRelease(port), Throws.Nothing, "the port must be free after dispose");
            Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync(CancellationToken.None));
        }
    }

    [Test]
    public async Task Stop_releases_the_port()
    {
        await using McpTestNode node = await McpTestNode.Create();
        int port = node.Endpoint.Port;

        await node.Host.StopAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Host.Endpoint, Is.Null);
            Assert.That(() => BindAndRelease(port), Throws.Nothing);
            Assert.That(async () => await node.Host.StopAsync(CancellationToken.None), Throws.Nothing, "stopping a stopped host is a no-op");
        }
    }

    private static void BindAndRelease(int port)
    {
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }
}
