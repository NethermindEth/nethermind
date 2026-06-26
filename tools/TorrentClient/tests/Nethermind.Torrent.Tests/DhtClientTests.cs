// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class DhtClientTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task Get_peers_requires_response_id_to_match_queried_node(bool matchingResponseId)
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] expectedId = CreateId(0x40);
        byte[] responseId = CreateId(matchingResponseId ? (byte)0x40 : (byte)0x41);
        Task responseTask = RespondOnceAsync(server, responseId, TestContext.CurrentContext.CancellationToken);
        await using DhtClient client = new(CreateId(0xaa), _ => { });
        DhtNode node = new(new KadId(expectedId), (IPEndPoint)server.Client.LocalEndPoint!);
        List<PeerEndpoint> peers = [];
        List<DhtNode> nodes = [];

        bool accepted = await QueryGetPeersAsync(
            client,
            node,
            CreateId(0x55),
            peers,
            nodes,
            TestContext.CurrentContext.CancellationToken);
        await responseTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(matchingResponseId));
            Assert.That(nodes, matchingResponseId ? Has.Count.EqualTo(1) : Is.Empty);
        }
    }

    [Test]
    public async Task Find_node_rejects_response_without_node_id()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        Task responseTask = RespondOnceAsync(server, responseId: null, TestContext.CurrentContext.CancellationToken);
        await using DhtClient client = new(CreateId(0xaa), _ => { });

        List<DhtNode> nodes = await QueryFindNodeAsync(
            client,
            (IPEndPoint)server.Client.LocalEndPoint!,
            CreateId(0x55),
            TestContext.CurrentContext.CancellationToken);
        await responseTask;

        Assert.That(nodes, Is.Empty);
    }

    [Test]
    public async Task Find_node_propagates_caller_cancellation()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        using CancellationTokenSource cts = new();
        await using DhtClient client = new(CreateId(0xaa), _ => { });
        Task<List<DhtNode>> queryTask = QueryFindNodeAsync(
            client,
            (IPEndPoint)server.Client.LocalEndPoint!,
            CreateId(0x55),
            cts.Token);

        _ = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        await cts.CancelAsync();
        Assert.That(
            async () => _ = await queryTask,
            Throws.InstanceOf<OperationCanceledException>());
    }

    private static async Task RespondOnceAsync(UdpClient server, byte[]? responseId, CancellationToken token)
    {
        UdpReceiveResult request = await server.ReceiveAsync(token);
        BDictionary query = BencodeDocument.Decode(request.Buffer).Root.AsDictionary("dht query");
        BString transaction = (BString)query["t"];
        List<KeyValuePair<string, BValue>> responseValues =
        [
            new("nodes", Bencode.Bytes(CreateCompactNode(0x70))),
        ];
        if (responseId is not null)
        {
            responseValues.Insert(0, new KeyValuePair<string, BValue>("id", Bencode.Bytes(responseId)));
        }

        byte[] payload = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("t", Bencode.Bytes(transaction.Bytes)),
            new KeyValuePair<string, BValue>("y", Bencode.String("r")),
            new KeyValuePair<string, BValue>("r", Bencode.Dictionary([.. responseValues]))));

        await server.SendAsync(payload, request.RemoteEndPoint);
    }

    private static async Task<bool> QueryGetPeersAsync(
        DhtClient client,
        DhtNode node,
        byte[] infoHash,
        List<PeerEndpoint> peers,
        List<DhtNode> nodes,
        CancellationToken token)
    {
        MethodInfo method = typeof(DhtClient).GetMethod(
            "QueryGetPeersAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(DhtNode),
                typeof(byte[]),
                typeof(List<PeerEndpoint>),
                typeof(CancellationToken),
                typeof(List<DhtNode>),
            ],
            modifiers: null)!;

        object? invocation = method.Invoke(client, [node, infoHash, peers, token, nodes]);
        Task<bool> task = (Task<bool>)invocation!;
        return await task;
    }

    private static async Task<List<DhtNode>> QueryFindNodeAsync(
        DhtClient client,
        IPEndPoint endpoint,
        byte[] target,
        CancellationToken token)
    {
        MethodInfo method = typeof(DhtClient).GetMethod(
            "QueryFindNodeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(IPEndPoint),
                typeof(byte[]),
                typeof(CancellationToken),
            ],
            modifiers: null)!;

        object? invocation = method.Invoke(client, [endpoint, target, token]);
        Task<List<DhtNode>> task = (Task<List<DhtNode>>)invocation!;
        return await task;
    }

    private static byte[] CreateCompactNode(byte first)
    {
        byte[] compactNode = new byte[26];
        CreateId(first).CopyTo(compactNode, 0);
        IPAddress.Loopback.GetAddressBytes().CopyTo(compactNode, KadId.Length);
        BinaryPrimitives.WriteUInt16BigEndian(compactNode.AsSpan(KadId.Length + 4, 2), 6881);
        return compactNode;
    }

    private static byte[] CreateId(byte first)
    {
        byte[] bytes = new byte[KadId.Length];
        bytes[0] = first;
        return bytes;
    }
}
