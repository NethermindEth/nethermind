// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Authentication;
using Nethermind.Core.Specs;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Hosting;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.JsonRpc;
using Nethermind.Runner.Test.JsonRpc;
using Nethermind.Serialization.Json;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum;

public class JsonRpcRunnerTests
{
    private static readonly Func<string, CancellationToken, Task<IPAddress[]>> ResolveNodeLan = static (host, _) => host switch
    {
        "node.lan" => Task.FromResult<IPAddress[]>([IPAddress.Parse("10.0.8.103"), IPAddress.Parse("fd00::1"), IPAddress.Parse("10.0.8.103")]),
        _ => throw new AssertionException($"Unexpected lookup of '{host}'"),
    };

    [TestCase("127.0.0.1", "http://127.0.0.1:8545")]
    [TestCase("0.0.0.0", "http://0.0.0.0:8545")]
    [TestCase("[::1]", "http://[::1]:8545")]
    [TestCase("[::]", "http://[::]:8545")]
    [TestCase("localhost", "http://localhost:8545")]
    [TestCase("LOCALHOST", "http://LOCALHOST:8545")]
    [TestCase("*", "http://*:8545")]
    [TestCase("+", "http://+:8545")]
    public async Task ResolveAsync_keeps_hosts_Kestrel_binds_explicitly(string host, string expected) =>
        Assert.That(await JsonRpcListenUrls.ResolveAsync([CreateUrl(host, 8545)], ResolveNodeLan, CancellationToken.None), Is.EqualTo(new[] { expected }));

    [Test]
    public async Task ResolveAsync_binds_host_name_to_each_resolved_address() =>
        Assert.That(
            await JsonRpcListenUrls.ResolveAsync([CreateUrl("node.lan", 8545), CreateUrl("node.lan", 8551)], ResolveNodeLan, CancellationToken.None),
            Is.EqualTo(new[]
            {
                "http://10.0.8.103:8545",
                "http://[fd00::1]:8545",
                "http://10.0.8.103:8551",
                "http://[fd00::1]:8551",
            }));

    [Test]
    public void ResolveAsync_throws_when_host_name_cannot_be_resolved([Values] bool lookupThrows) =>
        Assert.That(
            () => JsonRpcListenUrls.ResolveAsync(
                [CreateUrl("node.lan", 8545)],
                lookupThrows ? static (_, _) => throw new SocketException((int)SocketError.HostNotFound) : static (_, _) => Task.FromResult<IPAddress[]>([]),
                CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("node.lan"));

    [Test]
    public void ResolveAsync_throws_when_host_name_resolves_to_unspecified_address([Values("0.0.0.0", "::", "::ffff:0.0.0.0")] string unspecified) =>
        Assert.That(
            () => JsonRpcListenUrls.ResolveAsync(
                [CreateUrl("node.lan", 8545)],
                (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("10.0.8.103"), IPAddress.Parse(unspecified)]),
                CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("unspecified"));

    [Test]
    public async Task ResolveAsync_passes_cancellation_to_lookup()
    {
        using CancellationTokenSource cts = new();
        TaskCompletionSource lookupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string[]> lookup = JsonRpcListenUrls.ResolveAsync(
            [CreateUrl("node.lan", 8545)],
            async (_, token) =>
            {
                lookupStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return [];
            },
            cts.Token);

        await lookupStarted.Task;
        cts.Cancel();

        Assert.That(() => lookup, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Start_returns_when_host_lookup_is_cancelled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        JsonRpcUrlCollection urls = new(LimboLogs.Instance, new JsonRpcConfig { Enabled = true, Host = "node.lan" }, false);
        await using JsonRpcRunner runner = new(null!, urls, null!, null!, null!, LimboLogs.Instance, [], null!, null!, null!, null!, null!, null!);

        await runner.Start(cts.Token);
    }

    /// <summary>
    /// Runs the production host wiring: <see cref="JsonRpcRunner"/>'s WebHost signals shutdown through its own
    /// application lifetime, which the WebSocket pipeline has to observe to close open sockets.
    /// </summary>
    [Test]
    [NonParallelizable]
    public async Task Dispose_closes_open_WebSockets_with_going_away()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        int port = FreePort();
        JsonRpcConfig rpcConfig = new() { Enabled = true, Host = IPAddress.Loopback.ToString(), Port = port, WebSocketsPort = port };
        Bootstrap.Instance.JsonRpcService = Substitute.For<IJsonRpcService>();
        Bootstrap.Instance.LogManager = LimboLogs.Instance;
        Bootstrap.Instance.JsonSerializer = new EthereumJsonSerializer();
        Bootstrap.Instance.JsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        Bootstrap.Instance.JsonRpcAuthentication = Substitute.For<IRpcAuthentication>();
        await using JsonRpcRunner runner = new(
            Substitute.For<IJsonRpcProcessor>(),
            new JsonRpcUrlCollection(LimboLogs.Instance, rpcConfig, includeWebSockets: true),
            StartupTests.JsonRpcWebSocketsManager(),
            new ConfigProvider(rpcConfig, new InitConfig { WebSocketsEnabled = true }, new HealthChecksConfig()),
            Substitute.For<IRpcAuthentication>(),
            LimboLogs.Instance,
            [],
            Substitute.For<ITxPool>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IReceiptFinder>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISyncPeerPool>(),
            Substitute.For<IMainProcessingContext>());
        await runner.Start(deadline.Token);
        using ClientWebSocket client = new();
        await client.ConnectAsync(new Uri($"ws://{IPAddress.Loopback}:{port}"), deadline.Token);
        Task<WebSocketReceiveResult> received = client.ReceiveAsync(new byte[128], deadline.Token);

        Stopwatch stopwatch = Stopwatch.StartNew();
        ValueTask stopped = runner.DisposeAsync();
        WebSocketReceiveResult result = await received;
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token);
        await stopped;
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
            Assert.That(result.CloseStatus, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            // Without the close, the host waits a minute for the open socket before aborting it.
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        }
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static JsonRpcUrl CreateUrl(string host, int port) =>
        new(Uri.UriSchemeHttp, host, port, RpcEndpoint.Http, false, ["eth"]);
}
