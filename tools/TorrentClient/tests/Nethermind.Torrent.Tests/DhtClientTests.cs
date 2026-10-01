// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

    [TestCase("", "4a533d47ec9c7d95b1ad75f576cffc641853b750", "305ac8aeb6c9c151fa120f120ea2cfb923564e11552d06a5d856091e5e853cff1260d3f39e4999684aa92eb73ffd136e6f4f3ecbfda0ce53a1608ecd7ae21f01")]
    [TestCase("foobar", "411eba73b6f087ca51a3795d9c8c938d365e32c1", "6834284b6b24c3204eb2fea824d82f88883a3d95e8b4a21b8c0ded553d17d17ddf9a8a7104b1258f30bed3787e6cb896fca78c58f8e03b5f18f14951a87d9a08")]
    public void Mutable_item_matches_bep44_vectors(string salt, string targetHex, string signatureHex)
    {
        byte[] value = Bencode.Encode(Bencode.String("Hello World!"));
        DhtMutableItem item = DhtMutableItem.FromSigned(
            Convert.FromHexString("77ff84905a91936367c01360803104f92432fcd904a43511876df5cdf3e7e548"),
            Encoding.ASCII.GetBytes(salt), 1, Convert.FromHexString(signatureHex), value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Convert.ToHexString(item.PublicKey), Is.EqualTo("77FF84905A91936367C01360803104F92432FCD904A43511876DF5CDF3E7E548"));
            Assert.That(Convert.ToHexString(item.Target), Is.EqualTo(targetHex.ToUpperInvariant()));
            Assert.That(Convert.ToHexString(item.Signature), Is.EqualTo(signatureHex.ToUpperInvariant()));
        }
        Assert.DoesNotThrow(() => DhtMutableItem.FromSigned(item.PublicKey, item.Salt, item.Sequence, item.Signature, item.Value));
    }

    [Test]
    public void Immutable_item_matches_bep44_vector()
        => Assert.That(Convert.ToHexString(SHA1.HashData(Bencode.Encode(Bencode.String("Hello World!")))),
            Is.EqualTo("E5F96F6F38320F0F33959CB4D3D656452117AADB"));

    [Test]
    public void Mutable_item_rejects_tampering_and_noncanonical_value()
    {
        DhtMutableItem item = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.String("hello")), 1);
        byte[] signature = item.Signature.ToArray();
        signature[0] ^= 1;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => DhtMutableItem.FromSigned(item.PublicKey, [], 1, signature, item.Value), Throws.TypeOf<FormatException>());
            Assert.That(() => DhtMutableItem.FromSigned(item.PublicKey, [1], 1, item.Signature, item.Value), Throws.TypeOf<FormatException>());
            Assert.That(() => DhtMutableItem.Sign(new byte[32], "i01e"u8, 1), Throws.TypeOf<FormatException>());
            Assert.That(() => DhtMutableItem.Sign(new byte[32], new byte[1001], 1), Throws.TypeOf<FormatException>());
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Mutable_get_verifies_the_signed_network_response(bool validSignature)
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] nodeId = CreateId(0x40);
        DhtMutableItem item = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("ih", Bencode.Bytes(CreateId(0x55))))), 7);
        byte[] signature = item.Signature.ToArray();
        if (!validSignature)
        {
            signature[0] ^= 1;
        }

        Task responseTask = RespondItemGetAsync(server, nodeId, item, signature);
        await using DhtClient client = CreateClient(server, nodeId);
        DhtMutableItem? actual = await client.GetMutableAsync(item.PublicKey, [], TestContext.CurrentContext.CancellationToken);
        await responseTask;

        Assert.That(actual?.Sequence, validSignature ? Is.EqualTo(7) : Is.Null);
        if (actual is not null)
        {
            Assert.That(Bep46Link.Decode(actual).InfoHash, Is.EqualTo(CreateId(0x55)));
        }
    }

    [Test]
    public async Task Mutable_get_selects_highest_valid_sequence_across_nodes()
    {
        using UdpClient first = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient second = new(new IPEndPoint(IPAddress.Loopback, 0));
        DhtMutableItem old = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.String("old")), 3);
        DhtMutableItem latest = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.String("new")), 4);
        Task firstResponse = RespondItemGetAsync(first, CreateId(0x40), old, old.Signature,
            CreateCompactNode(CreateId(0x41), (IPEndPoint)second.Client.LocalEndPoint!));
        Task secondResponse = RespondItemGetAsync(second, CreateId(0x41), latest, latest.Signature);
        await using DhtClient client = CreateClient(first, CreateId(0x40));

        DhtMutableItem? item = await client.GetMutableAsync(old.PublicKey, [], TestContext.CurrentContext.CancellationToken);
        await Task.WhenAll(firstResponse, secondResponse);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item?.Sequence, Is.EqualTo(4));
            Assert.That(item?.Value, Is.EqualTo(latest.Value));
        }
    }

    [Test]
    public async Task Mutable_get_rejects_conflicting_values_at_the_same_sequence()
    {
        using UdpClient first = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient second = new(new IPEndPoint(IPAddress.Loopback, 0));
        DhtMutableItem firstItem = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.String("first")), 5);
        DhtMutableItem secondItem = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.String("second")), 5);
        Task firstResponse = RespondItemGetAsync(first, CreateId(0x40), firstItem, firstItem.Signature,
            CreateCompactNode(CreateId(0x41), (IPEndPoint)second.Client.LocalEndPoint!));
        Task secondResponse = RespondItemGetAsync(second, CreateId(0x41), secondItem, secondItem.Signature);
        await using DhtClient client = CreateClient(first, CreateId(0x40));

        Assert.That(async () => await client.GetMutableAsync(firstItem.PublicKey, [], TestContext.CurrentContext.CancellationToken),
            Throws.TypeOf<InvalidDataException>());
        await Task.WhenAll(firstResponse, secondResponse);
    }

    [Test]
    public async Task Mutable_get_reaches_a_live_node_after_six_unresponsive_candidates()
    {
        using UdpClient live = new(new IPEndPoint(IPAddress.Loopback, 0));
        DhtMutableItem item = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.String("available")), 1);
        byte[] liveId = item.Target.ToArray();
        liveId[^1] ^= 7;
        List<UdpClient> silent = [];
        try
        {
            await using DhtClient client = CreateClient(live, liveId);
            TorrentKademlia routing = (TorrentKademlia)typeof(DhtClient)
                .GetField("_kademlia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            for (byte distance = 1; distance <= 6; distance++)
            {
                UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
                silent.Add(server);
                byte[] id = item.Target.ToArray();
                id[^1] ^= distance;
                routing.AddOrRefresh(new DhtNode(new KadId(id), (IPEndPoint)server.Client.LocalEndPoint!));
            }

            Task responseTask = RespondItemGetAsync(live, liveId, item, item.Signature);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CurrentContext.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            DhtMutableItem? actual = await client.GetMutableAsync(item.PublicKey, [], deadline.Token);
            await responseTask;

            Assert.That(actual?.Value, Is.EqualTo(item.Value));
        }
        finally
        {
            foreach (UdpClient server in silent)
            {
                server.Dispose();
            }
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Immutable_get_checks_content_hash(bool matchesTarget)
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] value = Bencode.Encode(Bencode.String("immutable"));
        byte[] target = matchesTarget ? SHA1.HashData(value) : new byte[20];
        Task responseTask = RespondImmutableGetAsync(server, CreateId(0x40), value);
        await using DhtClient client = CreateClient(server, CreateId(0x40));

        byte[]? actual = await client.GetImmutableAsync(target, TestContext.CurrentContext.CancellationToken);
        await responseTask;

        Assert.That(actual, matchesTarget ? Is.EqualTo(value) : Is.Null);
    }

    [Test]
    public async Task Immutable_put_sends_canonical_value_without_signature_fields()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] value = Bencode.Encode(Bencode.String("immutable"));
        Task<BDictionary> serverTask = CaptureItemPutAsync(server, CreateId(0x40));
        await using DhtClient client = CreateClient(server, CreateId(0x40));

        int count = await client.PutImmutableAsync(value, TestContext.CurrentContext.CancellationToken);
        BDictionary args = await serverTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(Bencode.Encode(args["v"]), Is.EqualTo(value));
            Assert.That(args.TryGetValue("k", out _), Is.False);
            Assert.That(args.TryGetValue("sig", out _), Is.False);
        }
    }

    [Test]
    public async Task Mutable_get_preserves_binary_dictionary_keys()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] value = [(byte)'d', (byte)'1', (byte)':', 0xff, (byte)'i', (byte)'1', (byte)'e', (byte)'e'];
        DhtMutableItem item = DhtMutableItem.Sign(new byte[32], value, 2);
        Task responseTask = RespondItemGetAsync(server, CreateId(0x40), item, item.Signature);
        await using DhtClient client = CreateClient(server, CreateId(0x40));

        DhtMutableItem? actual = await client.GetMutableAsync(item.PublicKey, [], TestContext.CurrentContext.CancellationToken);
        await responseTask;

        Assert.That(actual?.Value, Is.EqualTo(value));
    }

    [Test]
    public async Task Bep46_magnet_resolves_signed_dht_pointer_to_v1_magnet()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] infoHash = CreateId(0x55);
        byte[] salt = [0x6e];
        DhtMutableItem item = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("ih", Bencode.Bytes(infoHash)))), 9, salt);
        string uri = $"magnet:?xs=urn:btpk:{Convert.ToHexString(item.PublicKey)}&s=6e&x.pe=127.0.0.1:6881";
        Bep46Link feed = Bep46Link.Parse(uri);
        Task responseTask = RespondItemGetAsync(server, CreateId(0x40), item, item.Signature);
        await using DhtClient client = CreateClient(server, CreateId(0x40));

        Bep46Update? update = await feed.GetCurrentAsync(client.GetMutableAsync, TestContext.CurrentContext.CancellationToken);
        await responseTask;
        Assert.That(update, Is.Not.Null);
        MagnetLink resolved = MagnetLink.Parse(feed.ToMagnet(update!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(update!.Sequence, Is.EqualTo(9));
            Assert.That(resolved.InfoHash, Is.EqualTo(infoHash));
            Assert.That(resolved.ExplicitPeers, Is.EqualTo(new[] { "127.0.0.1:6881" }));
        }
    }

    [Test]
    public async Task Immutable_put_preserves_binary_dictionary_keys()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] value = [(byte)'d', (byte)'1', (byte)':', 0xff, (byte)'i', (byte)'1', (byte)'e', (byte)'e'];
        Task<BDictionary> serverTask = CaptureItemPutAsync(server, CreateId(0x40));
        await using DhtClient client = CreateClient(server, CreateId(0x40));

        int count = await client.PutImmutableAsync(value, TestContext.CurrentContext.CancellationToken);
        BDictionary args = await serverTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(args.RawItemValue, Is.EqualTo(value));
        }
    }

    [Test]
    public async Task Mutable_put_uses_get_token_and_sends_signed_value_and_cas()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] nodeId = CreateId(0x40);
        DhtMutableItem item = DhtMutableItem.Sign(new byte[32], Bencode.Encode(Bencode.String("value")), 8, [0x6e]);
        Task<BDictionary> serverTask = CaptureItemPutAsync(server, nodeId);
        await using DhtClient client = CreateClient(server, nodeId);

        int stored = await client.PutMutableAsync(item, 7, TestContext.CurrentContext.CancellationToken);
        BDictionary args = await serverTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.EqualTo(1));
            Assert.That(args["token"].AsBytes("token"), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(args["k"].AsBytes("k"), Is.EqualTo(item.PublicKey));
            Assert.That(args["sig"].AsBytes("sig"), Is.EqualTo(item.Signature));
            Assert.That(args["seq"].AsInteger("seq"), Is.EqualTo(8));
            Assert.That(args["cas"].AsInteger("cas"), Is.EqualTo(7));
            Assert.That(args["salt"].AsBytes("salt"), Is.EqualTo(new byte[] { 0x6e }));
        }
    }

    private static async Task RespondItemGetAsync(UdpClient server, byte[] nodeId, DhtMutableItem item, byte[] signature, byte[]? compactNodes = null)
    {
        UdpReceiveResult request = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        BDictionary args = ReadQueryArguments(request, "get");
        Assert.That(args["target"].AsBytes("target"), Is.EqualTo(item.Target));
        BDictionary response = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("id", Bencode.Bytes(nodeId)),
            new KeyValuePair<string, BValue>("nodes", Bencode.Bytes(compactNodes ?? [])),
            new KeyValuePair<string, BValue>("token", Bencode.Bytes([1, 2, 3])),
            new KeyValuePair<string, BValue>("k", Bencode.Bytes(item.PublicKey)),
            new KeyValuePair<string, BValue>("seq", Bencode.Integer(item.Sequence)),
            new KeyValuePair<string, BValue>("sig", Bencode.Bytes(signature)),
            new KeyValuePair<string, BValue>("v", new BRaw(item.Value)));
        await SendItemResponseAsync(server, request, response);
    }

    private static async Task RespondImmutableGetAsync(UdpClient server, byte[] nodeId, byte[] value)
    {
        UdpReceiveResult request = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        _ = ReadQueryArguments(request, "get");
        await SendItemResponseAsync(server, request, Bencode.Dictionary(
            new KeyValuePair<string, BValue>("id", Bencode.Bytes(nodeId)),
            new KeyValuePair<string, BValue>("nodes", Bencode.Bytes([])),
            new KeyValuePair<string, BValue>("v", BencodeDocument.Decode(value).Root)));
    }

    private static async Task<BDictionary> CaptureItemPutAsync(UdpClient server, byte[] nodeId)
    {
        UdpReceiveResult get = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        _ = ReadQueryArguments(get, "get");
        await SendItemResponseAsync(server, get, Bencode.Dictionary(
            new KeyValuePair<string, BValue>("id", Bencode.Bytes(nodeId)),
            new KeyValuePair<string, BValue>("nodes", Bencode.Bytes([])),
            new KeyValuePair<string, BValue>("token", Bencode.Bytes([1, 2, 3]))));
        UdpReceiveResult put = await server.ReceiveAsync(TestContext.CurrentContext.CancellationToken);
        BDictionary args = ReadQueryArguments(put, "put");
        await SendItemResponseAsync(server, put, Bencode.Dictionary(new KeyValuePair<string, BValue>("id", Bencode.Bytes(nodeId))));
        return args;
    }

    private static async Task SendItemResponseAsync(UdpClient server, UdpReceiveResult request, BDictionary response)
    {
        BDictionary query = BencodeDocument.Decode(request.Buffer).Root.AsDictionary("query");
        byte[] payload = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("t", query["t"]),
            new KeyValuePair<string, BValue>("y", Bencode.String("r")),
            new KeyValuePair<string, BValue>("r", response)));
        await server.SendAsync(payload, request.RemoteEndPoint);
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
