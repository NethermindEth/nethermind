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

    [Test]
    public async Task Announce_sends_matching_token_identity_port_and_seed_flag([Values] bool isSeed)
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] nodeId = CreateId(0x40);
        byte[] infoHash = CreateId(0x55);
        byte[] writeToken = [0, 1, 2, 255];
        Task<BDictionary> serverTask = CaptureAnnounceAsync(server, nodeId, infoHash, writeToken, isSeed);
        await using DhtClient client = CreateClient(server, nodeId);

        int count = await client.AnnounceAsync(infoHash, 49152, isSeed, TestContext.CurrentContext.CancellationToken);
        BDictionary args = await serverTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(args["port"].AsInteger("port"), Is.EqualTo(49152));
            Assert.That(args["info_hash"].AsBytes("info_hash"), Is.EqualTo(infoHash));
            Assert.That(args["token"].AsBytes("token"), Is.EqualTo(writeToken));
            Assert.That(args.TryGetValue("implied_port", out _), Is.False);
            Assert.That(args.TryGetValue("seed", out BValue? seed), Is.EqualTo(isSeed));
            if (isSeed)
            {
                Assert.That(seed!.AsInteger("seed"), Is.EqualTo(1));
            }
        }
    }

    [TestCase("missing")]
    [TestCase("empty")]
    [TestCase("wrong-id")]
    public async Task Announce_skips_responses_without_a_valid_token_from_the_queried_node(string responseKind)
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] nodeId = CreateId(0x40);
        Task serverTask = RespondGetPeersAsync(
            server,
            responseKind == "wrong-id" ? CreateId(0x41) : nodeId,
            responseKind == "missing" ? null : responseKind == "empty" ? [] : [1, 2, 3]);
        await using DhtClient client = CreateClient(server, nodeId);

        int count = await client.AnnounceAsync(CreateId(0x55), 6881, true, TestContext.CurrentContext.CancellationToken);
        await serverTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.Zero);
            Assert.That(server.Available, Is.Zero, "invalid token must not be announced");
        }
    }

    [Test]
    public async Task Announce_does_not_count_an_unanswered_announce()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] nodeId = CreateId(0x40);
        Task serverTask = RespondGetPeersThenIgnoreAnnounceAsync(server, nodeId);
        await using DhtClient client = CreateClient(server, nodeId);

        int count = await client.AnnounceAsync(CreateId(0x55), 6881, true, TestContext.CurrentContext.CancellationToken);
        await serverTask;

        Assert.That(count, Is.Zero);
    }

    [Test]
    public async Task Announce_returns_zero_when_get_peers_does_not_respond()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        await using DhtClient client = CreateClient(server, CreateId(0x40));

        int count = await client.AnnounceAsync(CreateId(0x55), 6881, true, TestContext.CurrentContext.CancellationToken);
        UdpReceiveResult query = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.Zero);
            Assert.That(ReadQueryName(query), Is.EqualTo("get_peers"));
            TorrentKademlia routing = (TorrentKademlia)typeof(DhtClient)
                .GetField("_kademlia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            Assert.That(routing.GetClosest(new KadId(CreateId(0x55)), 1), Is.Empty);
        }
    }

    [Test]
    public async Task Announce_propagates_caller_cancellation()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        using CancellationTokenSource cts = new();
        await using DhtClient client = CreateClient(server, CreateId(0x40));
        Task<int> announce = client.AnnounceAsync(CreateId(0x55), 6881, true, cts.Token);

        UdpReceiveResult query = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        Assert.That(ReadQueryName(query), Is.EqualTo("get_peers"));
        await cts.CancelAsync();

        Assert.That(async () => await announce, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Announce_queries_a_closer_node_and_uses_each_nodes_own_token()
    {
        using UdpClient firstServer = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient closerServer = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] firstId = CreateId(0x60);
        byte[] closerId = CreateId(0x01);
        byte[] infoHash = CreateId(0x00);
        Task<BDictionary> firstTask = CaptureAnnounceAsync(
            firstServer, firstId, infoHash, [1], true,
            compactNodes: CreateCompactNode(closerId, (IPEndPoint)closerServer.Client.LocalEndPoint!));
        Task<BDictionary> closerTask = CaptureAnnounceAsync(closerServer, closerId, infoHash, [2], true);
        await using DhtClient client = CreateClient(firstServer, firstId);

        int count = await client.AnnounceAsync(infoHash, 6881, true, TestContext.CurrentContext.CancellationToken);
        BDictionary[] announcements = await Task.WhenAll(firstTask, closerTask);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(2));
            Assert.That(announcements[0]["token"].AsBytes("token"), Is.EqualTo(new byte[] { 1 }));
            Assert.That(announcements[1]["token"].AsBytes("token"), Is.EqualTo(new byte[] { 2 }));
        }
    }

    [Test]
    public async Task Announce_does_not_count_a_reply_from_a_different_node_identity()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] nodeId = CreateId(0x40);
        Task<BDictionary> serverTask = CaptureAnnounceAsync(
            server, nodeId, CreateId(0x55), [1, 2, 3], true, announceResponseId: CreateId(0x41));
        await using DhtClient client = CreateClient(server, nodeId);

        int count = await client.AnnounceAsync(CreateId(0x55), 6881, true, TestContext.CurrentContext.CancellationToken);
        await serverTask;

        Assert.That(count, Is.Zero);
    }

    [Test]
    public async Task Concurrent_announces_do_not_compete_for_udp_responses()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] nodeId = CreateId(0x40);
        byte[] infoHash = CreateId(0x55);
        Task serverTask = RespondToAnnouncesAsync(server, nodeId, infoHash, 2);
        await using DhtClient client = CreateClient(server, nodeId);

        Task<int> first = client.AnnounceAsync(infoHash, 6881, true, TestContext.CurrentContext.CancellationToken);
        Task<int> second = client.AnnounceAsync(infoHash, 6881, true, TestContext.CurrentContext.CancellationToken);
        int[] counts = await Task.WhenAll(first, second);
        await serverTask;

        Assert.That(counts, Is.EqualTo(new[] { 1, 1 }));
    }

    [TestCase(0)]
    [TestCase(65536)]
    public async Task Announce_rejects_invalid_port(int port)
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        await using DhtClient client = CreateClient(server, CreateId(0x40));
        Assert.That(
            async () => await client.AnnounceAsync(CreateId(0x55), port, true, CancellationToken.None),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    private static DhtClient CreateClient(UdpClient server, byte[] nodeId)
    {
        DhtClient client = new(CreateId(0xaa), _ => { });
        FieldInfo field = typeof(DhtClient).GetField("_kademlia", BindingFlags.Instance | BindingFlags.NonPublic)!;
        TorrentKademlia kademlia = (TorrentKademlia)field.GetValue(client)!;
        kademlia.AddOrRefresh(new DhtNode(new KadId(nodeId), (IPEndPoint)server.Client.LocalEndPoint!));
        return client;
    }

    private static async Task<BDictionary> CaptureAnnounceAsync(
        UdpClient server, byte[] nodeId, byte[] infoHash, byte[] writeToken, bool isSeed,
        byte[]? compactNodes = null, byte[]? announceResponseId = null)
    {
        UdpReceiveResult getPeers = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        BDictionary getPeersArgs = ReadQueryArguments(getPeers, "get_peers");
        Assert.That(getPeersArgs["info_hash"].AsBytes("info_hash"), Is.EqualTo(infoHash));
        byte[] requesterId = getPeersArgs["id"].AsBytes("id");
        Assert.That(requesterId, Has.Length.EqualTo(KadId.Length));
        await SendResponseAsync(server, getPeers, nodeId, writeToken, compactNodes);

        UdpReceiveResult announce = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        BDictionary announceArgs = ReadQueryArguments(announce, "announce_peer");
        Assert.That(announceArgs["id"].AsBytes("id"), Is.EqualTo(requesterId));
        Assert.That(announceArgs.TryGetValue("seed", out _), Is.EqualTo(isSeed));
        await SendResponseAsync(server, announce, announceResponseId ?? nodeId, null);
        return announceArgs;
    }

    private static async Task RespondGetPeersAsync(UdpClient server, byte[] responseId, byte[]? writeToken)
    {
        UdpReceiveResult query = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        Assert.That(ReadQueryName(query), Is.EqualTo("get_peers"));
        await SendResponseAsync(server, query, responseId, writeToken);
    }

    private static async Task RespondGetPeersThenIgnoreAnnounceAsync(UdpClient server, byte[] nodeId)
    {
        await RespondGetPeersAsync(server, nodeId, [1, 2, 3]);
        UdpReceiveResult announce = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        Assert.That(ReadQueryName(announce), Is.EqualTo("announce_peer"));
    }

    private static async Task RespondToAnnouncesAsync(UdpClient server, byte[] nodeId, byte[] infoHash, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await CaptureAnnounceAsync(server, nodeId, infoHash, [1, 2, 3], true);
        }
    }

    private static async Task SendResponseAsync(
        UdpClient server, UdpReceiveResult request, byte[] nodeId, byte[]? writeToken, byte[]? compactNodes = null)
    {
        BDictionary query = BencodeDocument.Decode(request.Buffer).Root.AsDictionary("dht query");
        List<KeyValuePair<string, BValue>> values =
        [
            new("id", Bencode.Bytes(nodeId)),
        ];
        if (ReadQueryName(request) == "get_peers")
        {
            values.Add(new KeyValuePair<string, BValue>("nodes", Bencode.Bytes(compactNodes ?? [])));
        }

        if (writeToken is not null)
        {
            values.Add(new KeyValuePair<string, BValue>("token", Bencode.Bytes(writeToken)));
        }

        byte[] payload = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("t", query["t"]),
            new KeyValuePair<string, BValue>("y", Bencode.String("r")),
            new KeyValuePair<string, BValue>("r", Bencode.Dictionary([.. values]))));
        await server.SendAsync(payload, request.RemoteEndPoint);
    }

    private static string ReadQueryName(UdpReceiveResult request)
        => BencodeDocument.Decode(request.Buffer).Root.AsDictionary("dht query")["q"].AsText("q");

    private static BDictionary ReadQueryArguments(UdpReceiveResult request, string queryName)
    {
        BDictionary query = BencodeDocument.Decode(request.Buffer).Root.AsDictionary("dht query");
        Assert.That(query["q"].AsText("q"), Is.EqualTo(queryName));
        return query["a"].AsDictionary("a");
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

    private static byte[] CreateCompactNode(byte[] nodeId, IPEndPoint endpoint)
    {
        byte[] compactNode = new byte[26];
        nodeId.CopyTo(compactNode, 0);
        endpoint.Address.GetAddressBytes().CopyTo(compactNode, KadId.Length);
        BinaryPrimitives.WriteUInt16BigEndian(compactNode.AsSpan(KadId.Length + 4, 2), (ushort)endpoint.Port);
        return compactNode;
    }

    private static byte[] CreateId(byte first)
    {
        byte[] bytes = new byte[KadId.Length];
        bytes[0] = first;
        return bytes;
    }
}
