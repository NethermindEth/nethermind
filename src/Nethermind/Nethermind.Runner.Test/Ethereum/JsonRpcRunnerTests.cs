// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum;
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
    public async Task GetListenUrls_keeps_hosts_Kestrel_binds_explicitly(string host, string expected) =>
        Assert.That(await JsonRpcRunner.GetListenUrls([CreateUrl(host, 8545)], ResolveNodeLan, CancellationToken.None), Is.EqualTo(new[] { expected }));

    [Test]
    public async Task GetListenUrls_binds_host_name_to_each_resolved_address() =>
        Assert.That(
            await JsonRpcRunner.GetListenUrls([CreateUrl("node.lan", 8545), CreateUrl("node.lan", 8551)], ResolveNodeLan, CancellationToken.None),
            Is.EqualTo(new[]
            {
                "http://10.0.8.103:8545",
                "http://[fd00::1]:8545",
                "http://10.0.8.103:8551",
                "http://[fd00::1]:8551",
            }));

    [Test]
    public void GetListenUrls_throws_when_host_name_cannot_be_resolved([Values] bool lookupThrows) =>
        Assert.That(
            () => JsonRpcRunner.GetListenUrls(
                [CreateUrl("node.lan", 8545)],
                lookupThrows ? static (_, _) => throw new SocketException((int)SocketError.HostNotFound) : static (_, _) => Task.FromResult<IPAddress[]>([]),
                CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("node.lan"));

    [Test]
    public void GetListenUrls_throws_when_host_name_resolves_to_unspecified_address([Values("0.0.0.0", "::", "::ffff:0.0.0.0")] string unspecified) =>
        Assert.That(
            () => JsonRpcRunner.GetListenUrls(
                [CreateUrl("node.lan", 8545)],
                (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("10.0.8.103"), IPAddress.Parse(unspecified)]),
                CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("unspecified"));

    [Test]
    public async Task GetListenUrls_passes_cancellation_to_lookup()
    {
        using CancellationTokenSource cts = new();
        TaskCompletionSource lookupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string[]> lookup = JsonRpcRunner.GetListenUrls(
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

    private static JsonRpcUrl CreateUrl(string host, int port) =>
        new(Uri.UriSchemeHttp, host, port, RpcEndpoint.Http, false, ["eth"]);
}
