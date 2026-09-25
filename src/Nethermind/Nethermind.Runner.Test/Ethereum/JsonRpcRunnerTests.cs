// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Runner.Ethereum;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum;

public class JsonRpcRunnerTests
{
    private static readonly Func<string, IPAddress[]> ResolveNodeLan = static host => host switch
    {
        "node.lan" => [IPAddress.Parse("10.0.8.103"), IPAddress.Parse("fd00::1"), IPAddress.Parse("10.0.8.103")],
        _ => throw new AssertionException($"Unexpected lookup of '{host}'"),
    };

    [TestCase("127.0.0.1", "http://127.0.0.1:8545")]
    [TestCase("0.0.0.0", "http://0.0.0.0:8545")]
    [TestCase("[::1]", "http://[::1]:8545")]
    [TestCase("localhost", "http://localhost:8545")]
    [TestCase("*", "http://*:8545")]
    public void GetListenUrls_keeps_hosts_Kestrel_binds_explicitly(string host, string expected) =>
        Assert.That(JsonRpcRunner.GetListenUrls([CreateUrl(host, 8545)], ResolveNodeLan), Is.EqualTo(new[] { expected }));

    [Test]
    public void GetListenUrls_binds_host_name_to_each_resolved_address() =>
        Assert.That(
            JsonRpcRunner.GetListenUrls([CreateUrl("node.lan", 8545), CreateUrl("node.lan", 8551)], ResolveNodeLan),
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
                lookupThrows ? static _ => throw new SocketException((int)SocketError.HostNotFound) : static _ => []),
            Throws.InvalidOperationException.With.Message.Contains("node.lan"));

    private static JsonRpcUrl CreateUrl(string host, int port) =>
        new(Uri.UriSchemeHttp, host, port, RpcEndpoint.Http, false, ["eth"]);
}
