// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Test.Threading;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Discv5.Packets;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using Nethermind.Stats;
using Nethermind.Db;
using NSubstitute;
using NUnit.Framework;

using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

using System.Buffers.Binary;
using System.Linq;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class WireTests
{
    [Test]
    public async Task Inbound_findnode_walks_each_distance_once([Values(1, 257)] int count)
    {
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"));
        recipient.Kademlia.GetAllAtDistance(10).Returns([]);
        int[] distances = new int[count];
        Array.Fill(distances, 10);
        using FindNodeMsg request = new([1], distances);
        byte[] challenge = await RequestChallenge(initiator, recipient, request, 1);
        byte[] handshake = EncodeHandshake(initiator, request, challenge, out Session session);
        using (session)
        {
            await DeliverPacket(recipient, initiator.Endpoint, handshake);
            Packet packet;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) response), Is.True);
                Assert.That(initiator.PacketCodec.TryDecode(response.Data, out packet), Is.True);
            }
            using (packet)
            {
                Assert.That(PacketCodec.TryDecryptMessage(in packet, session.ReadKey, out Discv5Message message), Is.True);
                using (message)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(message, Is.TypeOf<NodesMsg>());
                        Assert.That(((NodesMsg)message).Records, Is.Empty);
                        Assert.That(message.RequestId, Is.EqualTo(request.RequestId));
                    }
                }
            }
        }
        recipient.Kademlia.Received(1).GetAllAtDistance(10);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Nodes_verify_signatures_only_for_matching_requests(CancellationToken token)
    {
        INodeRecordSigner signer = Substitute.For<INodeRecordSigner>();
        NodeRecordSigner realSigner = new(new Ecdsa());
        signer.Verify(Arg.Any<NodeRecord>()).Returns(call => realSigner.Verify(call.Arg<NodeRecord>()));
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"), recordSigner: signer);
        using TalkReqMsg talk = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] challenge = await RequestChallenge(initiator, recipient, talk, 1);
        byte[] handshake = EncodeHandshake(initiator, talk, challenge, out Session session);
        using (session)
        {
            await DeliverPacket(recipient, initiator.Endpoint, handshake);
            AssertTalkResponse(initiator, recipient, session, talk.RequestId);
            NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyC, IPAddress.Loopback);
            using NodesMsg unsolicited = new(new RequestId(2, 1), 1, [record]);
            await DeliverPacket(recipient, initiator.Endpoint, initiator.PacketCodec.EncodeOrdinary(
                TestItem.PublicKeyB, session.WriteKey, unsolicited, new byte[PacketCodec.NonceSize]));
            signer.DidNotReceive().Verify(Arg.Any<NodeRecord>());

            Task<Node[]?> pending = recipient.Adapter.FindNeighbours(new Node(TestItem.PublicKeyA, initiator.Endpoint), TestItem.PublicKeyC, token);
            Packet packet;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) sent), Is.True);
                Assert.That(initiator.PacketCodec.TryDecode(sent.Data, out packet), Is.True);
            }
            RequestId requestId;
            using (packet)
            {
                Assert.That(PacketCodec.TryDecryptMessage(in packet, session.ReadKey, out Discv5Message request), Is.True);
                using (request) requestId = request.RequestId;
            }

            using NodesMsg wrongRequest = new(requestId with { Value = requestId.Value ^ 1 }, 1, [record]);
            await DeliverPacket(recipient, initiator.Endpoint, initiator.PacketCodec.EncodeOrdinary(
                TestItem.PublicKeyB, session.WriteKey, wrongRequest, new byte[PacketCodec.NonceSize]));
            signer.DidNotReceive().Verify(Arg.Any<NodeRecord>());
            Assert.That(pending.IsCompleted, Is.False);

            using NodesMsg matching = new(requestId, 1, [record]);
            await DeliverPacket(recipient, initiator.Endpoint, initiator.PacketCodec.EncodeOrdinary(
                TestItem.PublicKeyB, session.WriteKey, matching, new byte[PacketCodec.NonceSize]));
            Node[]? result = await pending;
            signer.Received(1).Verify(Arg.Any<NodeRecord>());
            Assert.That(result, Has.Length.EqualTo(1));
            Assert.That(result![0].Id, Is.EqualTo(TestItem.PublicKeyC));
        }
    }

    [Test]
    public async Task Endpoint_checks_should_be_bounded_per_source([Values("192.0.2.1", "::ffff:192.0.2.1", "2001:db8::1")] string address)
    {
        await using TestPeer peer = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        for (int i = 0; i < 4; i++)
        {
            Assert.That(peer.Adapter.TryReserveEndpointCheck(new Node(TestItem.PublicKeyB, address, 30303 + i)), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Adapter.TryReserveEndpointCheck(new Node(TestItem.PublicKeyB, address, 30307)), Is.False);
            Assert.That(peer.Adapter.TryReserveEndpointCheck(new Node(TestItem.PublicKeyB, "192.0.2.2", 30303)), Is.True);
        }
        peer.Adapter.ReleaseEndpointCheck(new Node(TestItem.PublicKeyB, address, 30303));
        Assert.That(peer.Adapter.TryReserveEndpointCheck(new Node(TestItem.PublicKeyB, address, 30307)), Is.True);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task FindNode_timeout_should_count_once_against_responder(CancellationToken token)
    {
        await using TestPeer peer = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        Node remote = new(TestItem.PublicKeyB, "127.0.0.1", 10001);
        IRoutingTable<Node, ValueHash256> routing = Substitute.For<IRoutingTable<Node, ValueHash256>>();
        routing.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<bool>()).Returns([remote]);
        INodeHealthTracker<Node> health = Substitute.For<INodeHealthTracker<Node>>();
        LookupKNearestNeighbour<PublicKey, Node, ValueHash256> lookup = new(routing,
            new FromKeyNodeHashProvider<PublicKey, Node, ValueHash256>(new PublicKeyKeyOperator()),
            ValueHash256KademliaDistance.Instance, health,
            new KademliaConfig<Node> { CurrentNodeId = new Node(TestItem.PublicKeyA, peer.Endpoint) });

        await lookup.Lookup(remote.IdHash.ValueHash256, 8,
            (node, cancellation) => peer.Adapter.FindNeighbours(node, TestItem.PublicKeyC, cancellation), token);

        health.Received(1).OnRequestFailed(remote);
        health.DidNotReceive().OnIncomingMessageFrom(Arg.Any<Node>());
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Request_capacity_should_refuse_new_work_without_displacing_oldest_handler(CancellationToken token)
    {
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"), pingTimeout: 30000);
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"));
        Node remote = new(TestItem.PublicKeyB, recipient.Endpoint);
        remote.SetVerifiedEnr(recipient.NodeRecordProvider.Current);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        List<Task<bool>> requests = [];
        try
        {
            for (int i = 0; i < 1025; i++) requests.Add(initiator.Adapter.Ping(remote, cancellation.Token));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(requests[^1].IsFaulted, Is.True);
                Assert.That(requests[^1].Exception!.GetBaseException(), Is.TypeOf<KademliaRequestNotSentException>());
            }

            Assert.That(initiator.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) initial), Is.True);
            initiator.Outbound.Sent.Clear();
            await DeliverPacket(recipient, initiator.Endpoint, initial.Data);
            Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) challenge), Is.True);
            await DeliverPacket(initiator, recipient.Endpoint, challenge.Data);
            Assert.That(initiator.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) handshake), Is.True);
            await DeliverPacket(recipient, initiator.Endpoint, handshake.Data);
            Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) response), Is.True);
            await DeliverPacket(initiator, recipient.Endpoint, response.Data);
            Assert.That(await requests[0].WaitAsync(token), Is.True);
        }
        finally
        {
            await cancellation.CancelAsync();
            await ((Task)Task.WhenAll(requests)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Challenge_ShouldEchoLatestNonce_AndPermitHandshake([Values] bool replaceChallenge)
    {
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"));
        using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] challenge = await RequestChallenge(initiator, recipient, request, 1);
        if (replaceChallenge) challenge = await RequestChallenge(initiator, recipient, request, 2);

        byte[] handshake = EncodeHandshake(initiator, request, challenge, out Session session);
        using (session)
        {
            await DeliverPacket(recipient, initiator.Endpoint, handshake);
            AssertTalkResponse(initiator, recipient, session, request.RequestId);
        }
    }

    [Test]
    public async Task InvalidHandshake_ShouldNotConsumeChallenge()
    {
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"));
        using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] challenge = await RequestChallenge(initiator, recipient, request, 1);
        byte[] handshake = EncodeHandshake(initiator, request, challenge, out Session session);
        using (session)
        {
            byte[] invalidHandshake = (byte[])handshake.Clone();
            invalidHandshake[^1] ^= 1;
            await DeliverPacket(recipient, initiator.Endpoint, invalidHandshake);
            Assert.That(recipient.Outbound.Sent, Is.Empty);

            await DeliverPacket(recipient, initiator.Endpoint, handshake);
            AssertTalkResponse(initiator, recipient, session, request.RequestId);
        }
    }

    [Test]
    public async Task ExpiredChallenge_ShouldNotAuthorizeHandshake([Values(60, 61)] int elapsedSeconds)
    {
        ManualTimeProvider time = new();
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"), timeProvider: time);
        using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] challenge = await RequestChallenge(initiator, recipient, request, 1);
        byte[] handshake = EncodeHandshake(initiator, request, challenge, out Session session);
        using (session)
        {
            time.Advance(TimeSpan.FromSeconds(elapsedSeconds));
            await DeliverPacket(recipient, initiator.Endpoint, handshake);
            if (elapsedSeconds == 60)
            {
                AssertTalkResponse(initiator, recipient, session, request.RequestId);
            }
            else
            {
                Assert.That(recipient.Outbound.Sent, Is.Empty);
                byte[] freshChallenge = await RequestChallenge(initiator, recipient, request, 2);
                byte[] freshHandshake = EncodeHandshake(initiator, request, freshChallenge, out Session freshSession);
                using (freshSession)
                {
                    await DeliverPacket(recipient, initiator.Endpoint, freshHandshake);
                    AssertTalkResponse(initiator, recipient, freshSession, request.RequestId);
                }
            }
        }
    }

    [Test]
    public async Task ReplacedChallenge_ShouldSurviveEarlierHandshakeVerification()
    {
        using CancellationTokenSource deadline = new(10_000);
        using ManualResetEventSlim continueVerification = new(false);
        TaskCompletionSource verificationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IEcdsa realEcdsa = new Ecdsa();
        IEcdsa ecdsa = Substitute.For<IEcdsa>();
        int verificationCalls = 0;
        ecdsa.RecoverCompressedPublicKey(Arg.Any<Signature>(), Arg.Any<ValueHash256>()).Returns(call =>
        {
            if (Interlocked.Increment(ref verificationCalls) == 1)
            {
                verificationStarted.TrySetResult();
                continueVerification.Wait(deadline.Token);
            }
            return realEcdsa.RecoverCompressedPublicKey(call.Arg<Signature>(), call.Arg<ValueHash256>());
        });
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"), ecdsa: ecdsa);
        using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] firstChallenge = await RequestChallenge(initiator, recipient, request, 1);
        byte[] firstHandshake = EncodeHandshake(initiator, request, firstChallenge, out Session firstSession);
        using (firstSession)
        {
            Task verifying = Task.Run(() => DeliverPacket(recipient, initiator.Endpoint, firstHandshake), deadline.Token);
            byte[] latestChallenge;
            try
            {
                await verificationStarted.Task.WaitAsync(deadline.Token);
                latestChallenge = await RequestChallenge(initiator, recipient, request, 2);
            }
            finally
            {
                continueVerification.Set();
                await verifying;
            }
            Assert.That(recipient.Outbound.Sent, Is.Empty);

            byte[] latestHandshake = EncodeHandshake(initiator, request, latestChallenge, out Session latestSession);
            using (latestSession)
            {
                await DeliverPacket(recipient, initiator.Endpoint, latestHandshake);
                AssertTalkResponse(initiator, recipient, latestSession, request.RequestId);
            }
        }
    }

    [Test]
    public async Task Challenges_ShouldRemainBounded_WhenEndpointsExceedCapacity()
    {
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"),
            challengeLimiter: new AddressBurstLimiter(NodeFilter.AcceptAll));
        using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] ordinary = initiator.PacketCodec.EncodeOrdinary(TestItem.PublicKeyB, new byte[Session.KeySize], request, new byte[PacketCodec.NonceSize]);
        byte[]? oldestChallenge = null;
        byte[]? latestChallenge = null;
        IPEndPoint oldestEndpoint = new(IPAddress.Parse("10.1.0.0"), 10000);
        IPEndPoint latestEndpoint = oldestEndpoint;
        for (int i = 0; i < 8193; i++)
        {
            latestEndpoint = new IPEndPoint(new IPAddress(new byte[] { 10, 1, (byte)(i >> 8), (byte)i }), 10000);
            await DeliverPacket(recipient, latestEndpoint, ordinary);
            Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) response), Is.True);
            oldestChallenge ??= response.Data;
            latestChallenge = response.Data;
        }

        Assert.That(recipient.Adapter.RetainedChallengeCount, Is.EqualTo(4096));
        byte[] oldHandshake = EncodeHandshake(initiator, request, oldestChallenge!, out Session oldSession);
        using (oldSession)
        {
            await DeliverPacket(recipient, oldestEndpoint, oldHandshake);
            Assert.That(recipient.Outbound.Sent, Is.Empty);
        }
        byte[] latestHandshake = EncodeHandshake(initiator, request, latestChallenge!, out Session latestSession);
        using (latestSession)
        {
            await DeliverPacket(recipient, latestEndpoint, latestHandshake);
            AssertTalkResponse(initiator, recipient, latestSession, request.RequestId);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task RepeatedOrdinaryPackets_ShouldRespectChallengeLimit()
    {
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"),
            challengeLimiter: new AddressBurstLimiter(16, 8192, TimeSpan.FromDays(1)));
        using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] ordinary = initiator.PacketCodec.EncodeOrdinary(TestItem.PublicKeyB, new byte[Session.KeySize], request, new byte[PacketCodec.NonceSize]);
        await DeliverPacket(recipient, initiator.Endpoint, ordinary);
        recipient.Outbound.Sent.Clear();
        for (int i = 1; i < 16; i++) Assert.That(recipient.Adapter.TryAcceptChallenge(initiator.Endpoint), Is.True);
        Assert.That(recipient.Adapter.TryAcceptChallenge(initiator.Endpoint), Is.False);

        await DeliverPacket(recipient, initiator.Endpoint, ordinary);

        Assert.That(recipient.Outbound.Sent, Is.Empty);
    }

    private static async Task<byte[]> RequestChallenge(TestPeer initiator, TestPeer recipient, Discv5Message request, byte nonceByte)
    {
        byte[] nonce = new byte[PacketCodec.NonceSize];
        nonce[0] = nonceByte;
        byte[] ordinary = initiator.PacketCodec.EncodeOrdinary(TestItem.PublicKeyB, new byte[Session.KeySize], request, nonce);
        await DeliverPacket(recipient, initiator.Endpoint, ordinary);
        byte[]? challenge = null;
        List<(byte[] Data, IPEndPoint Destination)> deferred = [];
        int pending = recipient.Outbound.Sent.Count;
        for (int index = 0; index < pending && recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) response); index++)
        {
            bool matches = false;
            if (response.Destination.Equals(initiator.Endpoint) && initiator.PacketCodec.TryDecode(response.Data, out Packet packet))
            {
                using (packet) matches = packet.Flag == PacketFlag.WhoAreYou && packet.Nonce.Span.SequenceEqual(nonce);
            }
            if (matches && challenge is null) challenge = response.Data;
            else deferred.Add(response);
        }
        foreach ((byte[] Data, IPEndPoint Destination) packet in deferred) recipient.Outbound.Sent.Enqueue(packet);
        Assert.That(challenge, Is.Not.Null, "A challenge must match the requesting identity, endpoint and nonce.");
        return challenge!;
    }

    private static byte[] EncodeHandshake(TestPeer initiator, Discv5Message request, byte[] challengeBytes, out Session session)
    {
        Assert.That(initiator.PacketCodec.TryDecode(challengeBytes, out Packet packet), Is.True);
        using (packet)
        using (Challenge challenge = initiator.PacketCodec.DecodeWhoAreYou(in packet))
        {
            return initiator.PacketCodec.EncodeHandshake(TestItem.PublicKeyB, challenge, request, initiator.NodeRecordProvider.Current, out session);
        }
    }

    private static async Task DeliverPacket(TestPeer recipient, IPEndPoint sender, byte[] data, CancellationToken token = default)
    {
        PooledUdpReceiveResult packet = PooledUdpReceiveResult.Copy(data, sender);
        try
        {
            await recipient.Adapter.HandlePacket(packet, token);
        }
        finally
        {
            packet.Dispose();
        }
    }

    private static void AssertTalkResponse(TestPeer initiator, TestPeer recipient, Session session, RequestId requestId)
    {
        Packet packet;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) response), Is.True);
            Assert.That(initiator.PacketCodec.TryDecode(response.Data, out packet), Is.True);
        }
        using (packet)
        {
            Assert.That(PacketCodec.TryDecryptMessage(in packet, session.ReadKey, out Discv5Message message), Is.True);
            using (message)
            using (Assert.EnterMultipleScope())
            {
                Assert.That(message, Is.TypeOf<TalkRespMsg>());
                Assert.That(message.RequestId, Is.EqualTo(requestId));
            }
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Ping_timeout_does_not_throw_but_caller_cancellation_does(CancellationToken token)
    {
        await using TestPeer peer = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"), pingTimeout: 100);
        Node remote = new(TestItem.PublicKeyB, IPEndPoint.Parse("127.0.0.1:10001"));
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        System.Threading.AsyncLocal<bool> observing = new() { Value = true };
        int exceptions = 0;
        void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (observing.Value && args.Exception is OperationCanceledException) Interlocked.Increment(ref exceptions);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnException;
        bool result;
        try
        {
            result = await peer.Adapter.Ping(remote, caller.Token);
        }
        finally
        {
            observing.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(exceptions, Is.Zero);
        }

        Task<bool> cancelled = peer.Adapter.Ping(remote, caller.Token);
        await caller.CancelAsync();
        Assert.That(async () => await cancelled, Throws.InstanceOf<OperationCanceledException>());
        peer.Kademlia.DidNotReceive().AddOrRefresh(Arg.Any<Node>());
    }

    [Test]
    public async Task Ping_Completes_After_WhoAreYou_Handshake([Values] bool afterStartupLoad)
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB);
        Assert.That(nodeB.SetVerifiedEnr(peerB.NodeRecordProvider.Current), Is.True);

        using MemDb db = new();
        NetworkStorage storage = new(db, LimboLogs.Instance);
        storage.UpdateNode(new NetworkNode(TestItem.PublicKeyC, "8.8.4.1", 30303, 0));
        storage.Commit();
        List<Node> live = [];
        peerA.Kademlia.When(table => table.AddOrRefresh(Arg.Any<Node>())).Do(call => { lock (live) live.Add(call.Arg<Node>()); });
        peerA.Kademlia.IterateNodes().Returns(_ => { lock (live) return live.ToArray(); });
        IKademliaMessageSender<PublicKey, Node> loadSender = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();
        loadSender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<bool>(new KademliaRequestNotSentException()));
        using PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Any, endpointA.Address),
            new LocalAddressSource(LimboLogs.Instance, () => []));
        DiscoveryPersistenceManager persistence = new(storage, Substitute.For<INodeStatsManager>(), loadSender,
            peerA.Kademlia, new DiscoveryConfig(), LimboLogs.Instance, policy)
        { RequestActivity = peerA.Adapter.RequestActivity };
        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Assert.That(peerA.Adapter.TryReserveEndpointCheck(nodeB), Is.True);
        TaskCompletionSource initialPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peerA.Outbound.OnSend = (_, _) => initialPacket.TrySetResult();
        Task<bool>? pingTask = afterStartupLoad ? null : peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        if (!afterStartupLoad) await initialPacket.Task.WaitAsync(cancellationSource.Token);
        peerA.Outbound.OnSend = null;
        await persistence.LoadPersistedNodes(cancellationSource.Token);
        persistence.CommitLiveNodes();
        Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId), Is.EqualTo(new[] { TestItem.PublicKeyC }));
        pingTask ??= peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        Assert.That(await pingTask, Is.True);

        await PumpUntil(() => HasReceivedNodeWithEnrSequence(peerB.Kademlia, TestItem.PublicKeyA,
            peerA.NodeRecordProvider.Current.EnrSequence), peerA, peerB, cancellationSource.Token);

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);
        peerA.Adapter.ReleaseEndpointCheck(nodeB);
        persistence.CommitLiveNodes();
        Assert.That(storage.GetPersistedNodes().Select(node => node.NodeId),
            Is.EquivalentTo(afterStartupLoad ? new[] { TestItem.PublicKeyB } : new[] { TestItem.PublicKeyB, TestItem.PublicKeyC }));


        peerA.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyB.PublicKey) && HasEnr(node)));
        peerB.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey) && HasEnr(node)));
    }

    [Test]
    public async Task Ping_Rehandshakes_After_RemoteSessionLost()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };

        using CancellationTokenSource cancellationSourceA = new(10_000);
        using CancellationTokenSource cancellationSourceB = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSourceA.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSourceB.Token);

        Task firstPing = peerA.Adapter.Ping(nodeB, cancellationSourceA.Token);
        await PumpUntilComplete(firstPing, peerA, peerB, cancellationSourceA.Token);
        await firstPing;

        await cancellationSourceB.CancelAsync();
        await runB;

        await using TestPeer restartedPeerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        using CancellationTokenSource cancellationSourceRestartedB = new(10_000);
        Task runRestartedB = restartedPeerB.Adapter.RunAsync(cancellationSourceRestartedB.Token);

        Task secondPing = peerA.Adapter.Ping(nodeB, cancellationSourceA.Token);
        await PumpUntilComplete(secondPing, peerA, restartedPeerB, cancellationSourceA.Token);
        await secondPing;

        await cancellationSourceA.CancelAsync();
        await cancellationSourceRestartedB.CancelAsync();
        await Task.WhenAll(runA, runRestartedB);
    }

    [Test]
    public async Task Ping_Refreshes_RemoteRecord_When_Pong_Advertises_Newer_Sequence()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA, realRouting: true);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB, enrSequence: 2);
        NodeRecord staleRecord = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            endpointB.Address,
            tcpPort: endpointB.Port,
            udpPort: endpointB.Port,
            enrSequence: 1);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = staleRecord
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;
        await PumpUntil(
            () => HasReceivedNodeWithEnrSequence(peerA.Kademlia, TestItem.PrivateKeyB.PublicKey, peerB.NodeRecordProvider.Current.EnrSequence),
            peerA,
            peerB,
            cancellationSource.Token);

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        Assert.That(
            HasReceivedNodeWithEnrSequence(peerA.Kademlia, TestItem.PrivateKeyB.PublicKey, peerB.NodeRecordProvider.Current.EnrSequence),
            Is.True);
    }

    [Test]
    public async Task Ping_Completes_With_HandshakeRecord_WithoutEndpoint()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA, includeEndpointInRecord: false);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        peerB.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node =>
            node.Id.Equals(TestItem.PrivateKeyA.PublicKey) &&
            !HasEnr(node) &&
            node.HighestObservedEnrSequence == peerA.NodeRecordProvider.Current.EnrSequence));
    }

    [TestCase(false, null, false, false)]
    [TestCase(true, null, true, false)]
    [TestCase(true, "2001:db8::2", false, false)]
    [TestCase(true, "2001:db8::2", false, true)]
    public async Task FindNeighbours_HandshakePreservesVerifiedEnrStateOnKnownNode(
        bool includeEndpointInRecord,
        string? recordIp,
        bool expectRecordReplacement,
        bool knownAsReplacement)
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        NodeRecord reachableRecord = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            endpointA.Address,
            tcpPort: endpointA.Port,
            udpPort: endpointA.Port,
            enrSequence: 1);
        Node knownNodeA = new(TestItem.PrivateKeyA.PublicKey, endpointA);
        knownNodeA.SetVerifiedEnr(reachableRecord);
        BoundedDistanceKademlia tableB = new(TestItem.PrivateKeyB.PublicKey.Hash, capacityPerDistance: 16);
        if (knownAsReplacement)
        {
            tableB.AddReplacement(knownNodeA);
        }
        else
        {
            tableB.AddOrRefresh(knownNodeA);
        }

        await using TestPeer peerA = CreatePeer(
            TestItem.PrivateKeyA,
            endpointA,
            includeEndpointInRecord,
            enrSequence: 2,
            recordIp: recordIp is null ? null : IPAddress.Parse(recordIp));
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB, kademlia: tableB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task<Node[]?> findTask = peerA.Adapter.FindNeighbours(nodeB, TestItem.PrivateKeyC.PublicKey, cancellationSource.Token);
        await PumpUntilComplete(findTask, peerA, peerB, cancellationSource.Token);
        Node[]? nodes = await findTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        Assert.That(nodes, Is.Not.Null);
        NodeRecord knownEnr = knownNodeA.Enr ?? throw new AssertionException("Expected the known node to retain its ENR.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(knownEnr.EnrSequence, Is.EqualTo(expectRecordReplacement ? 2 : 1));
            Assert.That(knownNodeA.IsVerifiedEnr(knownEnr), Is.True);
            Assert.That(knownNodeA.HighestObservedEnrSequence, Is.EqualTo(peerA.NodeRecordProvider.Current.EnrSequence));
        }
    }

    [Test]
    public async Task Ping_CachesEndpointlessSelfRecordSequenceWithoutRefetching()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        NodeRecord reachableRecord = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            endpointB.Address,
            tcpPort: endpointB.Port,
            udpPort: endpointB.Port,
            enrSequence: 1);
        Node knownNodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB);
        knownNodeB.SetVerifiedEnr(reachableRecord);
        BoundedDistanceKademlia tableA = new(TestItem.PrivateKeyA.PublicKey.Hash, capacityPerDistance: 16);
        tableA.AddOrRefresh(knownNodeB);

        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA, kademlia: tableA);
        await using TestPeer peerB = CreatePeer(
            TestItem.PrivateKeyB,
            endpointB,
            includeEndpointInRecord: false,
            enrSequence: 2);

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task firstPing = peerA.Adapter.Ping(knownNodeB, cancellationSource.Token);
        await PumpUntilComplete(firstPing, peerA, peerB, cancellationSource.Token);
        await firstPing;
        await PumpUntil(
            () => FindNode(tableA, TestItem.PrivateKeyB.PublicKey).HighestObservedEnrSequence == 2,
            peerA,
            peerB,
            cancellationSource.Token);
        int requestsAfterFirstPing = peerB.NodeRecordProvider.RequestCount;

        Node cachedNode = FindNode(tableA, TestItem.PrivateKeyB.PublicKey);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cachedNode.Enr, Is.SameAs(reachableRecord));
            Assert.That(cachedNode.IsVerifiedEnr(reachableRecord), Is.True);
            Assert.That(cachedNode.HighestObservedEnrSequence, Is.EqualTo(2));
        }

        Task secondPing = peerA.Adapter.Ping(cachedNode, cancellationSource.Token);
        await PumpUntilComplete(secondPing, peerA, peerB, cancellationSource.Token);
        await secondPing;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peerB.NodeRecordProvider.RequestCount, Is.EqualTo(requestsAfterFirstPing + 1));
            Assert.That(FindNode(tableA, TestItem.PrivateKeyB.PublicKey).HighestObservedEnrSequence, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task InboundPing_Starts_EndpointCheck_PingBack()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;

        await PumpUntil(
            () => peerB.Kademlia.ReceivedCallsMatching(
                kademlia => kademlia.AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey))),
                requiredNumberOfCalls: 2,
                maxNumberOfCalls: int.MaxValue),
            peerA,
            peerB,
            cancellationSource.Token);

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);
    }

    [Test]
    public async Task FindNeighbours_Returns_Records_At_Requested_Distance()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        IPEndPoint endpointC = IPEndPoint.Parse("127.0.0.1:10002");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        await using TestPeer peerC = CreatePeer(TestItem.PrivateKeyC, endpointC);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };
        Node nodeC = new(TestItem.PrivateKeyC.PublicKey, endpointC)
        {
            Enr = peerC.NodeRecordProvider.Current
        };
        using Distances requestedDistances = peerA.Adapter.GetLookupDistances(nodeB, TestItem.PrivateKeyC.PublicKey);
        for (int i = 0; i < requestedDistances.Count; i++)
        {
            peerB.Kademlia.GetAllAtDistance(requestedDistances[i]).Returns([]);
        }

        peerB.Kademlia.GetAllAtDistance(requestedDistances[0]).Returns([nodeC]);

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Node[]? nodes = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Task<Node[]?> findTask = peerA.Adapter.FindNeighbours(nodeB, TestItem.PrivateKeyC.PublicKey, cancellationSource.Token);
            await PumpUntilComplete(findTask, peerA, peerB, cancellationSource.Token);
            nodes = await findTask;
        }

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        Assert.That(nodes, Is.Not.Null);
        Assert.That(nodes, Has.Length.EqualTo(1));
        Assert.That(nodes![0].Id, Is.EqualTo(TestItem.PrivateKeyC.PublicKey));
        peerA.Kademlia.DidNotReceive().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyC.PublicKey)));
    }

    [Test]
    public async Task FindNeighbours_ShouldPreferValidatedRecords_WhenBucketHasMoreThanResponseLimit()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };

        Node[] bucketNodes = new Node[17];
        for (int i = 0; i < 16; i++)
        {
            bucketNodes[i] = CreateSignedNode(TestItem.PrivateKeys[i], IPEndPoint.Parse($"127.0.0.1:{11000 + i}"));
        }

        Node validatedNode = CreateSignedNode(TestItem.PrivateKeyD, IPEndPoint.Parse("127.0.0.1:12000"));
        validatedNode.ValidatedProtocol = true;
        bucketNodes[^1] = validatedNode;

        using Distances requestedDistances = peerA.Adapter.GetLookupDistances(nodeB, validatedNode.Id);
        for (int i = 0; i < requestedDistances.Count; i++)
        {
            peerB.Kademlia.GetAllAtDistance(requestedDistances[i]).Returns([]);
        }

        peerB.Kademlia.GetAllAtDistance(requestedDistances[0]).Returns(bucketNodes);

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task<Node[]?> findTask = peerA.Adapter.FindNeighbours(nodeB, validatedNode.Id, cancellationSource.Token);
        await PumpUntilComplete(findTask, peerA, peerB, cancellationSource.Token);
        Node[]? nodes = await findTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        Assert.That(nodes, Is.Not.Null);
        Assert.That(nodes, Has.Length.LessThanOrEqualTo(16));
        Assert.That(nodes, Has.One.Matches<Node>(node => node?.Id.Equals(validatedNode.Id) == true));
    }

    [Test]
    public async Task Ping_ShouldRefreshOnlyTargetLeafBeforeEviction([Values] bool incumbentResponds)
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        Node self = new(TestItem.PublicKeyA, endpointA);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [],
            new DiscoveryConfig { BucketSize = 2, BitsPerHop = 2 });
        config.NodeRequestFailureThreshold = 1;
        KBucketTree<Node, ValueHash256> routing = new(config,
            new FromKeyNodeHashProvider<PublicKey, Node, ValueHash256>(new PublicKeyKeyOperator()),
            ValueHash256KademliaDistance.Instance);
        List<PrivateKey>[] leaves = [[], [], [], []];
        foreach (PrivateKey key in TestItem.PrivateKeys)
        {
            ValueHash256 hash = key.PublicKey.Hash.ValueHash256;
            if (ValueHash256KademliaDistance.Instance.CalculateLogDistance(self.Id.Hash.ValueHash256, hash) != 256) continue;
            int leaf = (ValueHash256KademliaDistance.Instance.GetBit(hash, 1) ? 2 : 0)
                + (ValueHash256KademliaDistance.Instance.GetBit(hash, 2) ? 1 : 0);
            if (leaves[leaf].Count < 5) leaves[leaf].Add(key);
        }

        List<Node> incumbents = [];
        List<Node> replacements = [];
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            Assert.That(leaves[leaf], Has.Count.EqualTo(5));
            for (int i = 0; i < 2; i++)
            {
                Node node = CreateSignedNode(leaves[leaf][i], new IPEndPoint(IPAddress.Loopback, 11000 + leaf * 10 + i));
                node.IsStatic = i == 1;
                routing.TryAddOrRefresh(node.Id.Hash.ValueHash256, node, out _);
                incumbents.Add(node);
            }
        }
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            for (int i = 2; i < 4; i++)
            {
                Node node = CreateSignedNode(leaves[leaf][i], new IPEndPoint(IPAddress.Loopback, 11000 + leaf * 10 + i));
                Assert.That(routing.TryAddOrRefresh(node.Id.Hash.ValueHash256, node, out _), Is.EqualTo(BucketAddResult.Full));
                replacements.Add(node);
            }
        }
        Assert.That(routing.GetAllAtDistance(256), Has.Length.EqualTo(8));
        int removed = 0;
        TaskCompletionSource<Node> eviction = new(TaskCreationOptions.RunContinuationsAsynchronously);
        routing.OnNodeRemoved += (_, node) =>
        {
            Interlocked.Increment(ref removed);
            eviction.TrySetResult(node);
        };
        TaskCompletionSource<Node> refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IKademliaMessageSender<PublicKey, Node> sender = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();
        sender.Ping(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            refreshed.TrySetResult(call.Arg<Node>());
            return incumbentResponds;
        });
        await using NodeHealthTracker<PublicKey, Node, ValueHash256> health = new(config, routing,
            new FromKeyNodeHashProvider<PublicKey, Node, ValueHash256>(new PublicKeyKeyOperator()), sender);
        IKademlia<PublicKey, Node> table = Substitute.For<IKademlia<PublicKey, Node>>();
        table.When(t => t.AddOrRefresh(Arg.Any<Node>())).Do(call => health.OnIncomingMessageFrom(call.Arg<Node>()));
        table.GetAllAtDistance(Arg.Any<int>()).Returns(call => routing.GetAllAtDistance(call.Arg<int>()));
        table.When(t => t.Remove(Arg.Any<Node>())).Do(call => routing.Remove(call.Arg<Node>().Id.Hash.ValueHash256));

        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA, kademlia: table, bucketSize: 2, routing: routing);
        await using TestPeer peerB = CreatePeer(leaves[0][4], endpointB);
        Node nodeB = new(leaves[0][4].PublicKey, endpointB) { Enr = peerB.NodeRecordProvider.Current };
        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);
        try
        {
            Task<bool> ping = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
            await PumpUntilComplete(ping, peerA, peerB, cancellationSource.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await ping, Is.True);
                Assert.That(removed, Is.LessThanOrEqualTo(1));
            }
            Node refreshedNode = await refreshed.Task.WaitAsync(cancellationSource.Token);
            Assert.That(refreshedNode.Id, Is.EqualTo(incumbents[0].Id));
            if (!incumbentResponds)
            {
                Assert.That((await eviction.Task.WaitAsync(cancellationSource.Token)).Id, Is.EqualTo(refreshedNode.Id));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(removed, Is.EqualTo(incumbentResponds ? 0 : 1));
                Node[] stored = routing.GetAllAtDistance(256);
                Assert.That(stored, Has.Length.EqualTo(incumbentResponds ? 8 : 7));
                foreach (Node incumbent in incumbents)
                {
                    if (incumbentResponds || !ReferenceEquals(incumbent, refreshedNode))
                    {
                        Assert.That(stored, Does.Contain(incumbent));
                    }
                }
                for (int index = 2; index < replacements.Count; index++)
                {
                    Assert.That(routing.TryGet(replacements[index].Id.Hash.ValueHash256, out _), Is.True);
                }
            }
        }
        finally
        {
            await cancellationSource.CancelAsync();
            await Task.WhenAll(runA, runB);
        }
    }

    private static TestPeer CreatePeer(
        PrivateKey privateKey,
        IPEndPoint endpoint,
        bool includeEndpointInRecord = true,
        ulong enrSequence = 1,
        IKademlia<PublicKey, Node>? kademlia = null,
        int bucketSize = 16,
        IPAddress? recordIp = null,
        int pingTimeout = 1000,
        IRoutingTable<Node, ValueHash256>? routing = null,
        IEcdsa? ecdsa = null,
        TimeProvider? timeProvider = null,
        AddressBurstLimiter? challengeLimiter = null,
        INodeRecordSigner? recordSigner = null,
        int findNodeTimeout = 1000,
        DiscoveryV5Transport? inboundTransport = null,
        TimeProvider? requestTimeProvider = null,
        bool realRouting = false,
        ILogManager? logManager = null)
    {
        IKademlia<PublicKey, Node> table = kademlia ?? Substitute.For<IKademlia<PublicKey, Node>>();
        IRoutingTable<Node, ValueHash256> routingTable = routing ?? Substitute.For<IRoutingTable<Node, ValueHash256>>();
        if (table is BoundedDistanceKademlia boundedTable)
        {
            routingTable.TryGet(Arg.Any<ValueHash256>(), out _).Returns(callInfo =>
            {
                if (!boundedTable.TryGetNode((ValueHash256)callInfo[0], out Node storedNode))
                {
                    return false;
                }

                callInfo[1] = storedNode;
                return true;
            });
        }

        DiscoveryV5Transport transport = inboundTransport ?? new(new TestLogManager())
        {
            ChallengeLimiter = new(int.MaxValue, TimeSpan.FromMinutes(1))
        };
        RecordingDatagramSocket outbound = new();
        transport.BindSocket(outbound);

        TestNodeRecordProvider nodeRecordProvider = new(privateKey, endpoint, includeEndpointInRecord, enrSequence, recordIp);
        PacketCodec packetCodec = new(
            new InsecureProtectedPrivateKey(privateKey),
            new CryptoRandom(),
            ecdsa ?? new EthereumEcdsa(0));
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(endpoint.Address, endpoint.Address)));
        NetworkListenerState listenerState = new(
            new NetworkConfig { LocalIp = endpoint.Address.ToString() },
            ipResolver,
            LimboLogs.Instance);
        listenerState.SetRlpxAddress(endpoint.Address);
        listenerState.SetDiscoveryAddress(endpoint.Address);
        Node currentNode = new(privateKey.PublicKey, endpoint, true);
        KademliaConfig<Node> config = realRouting
            ? DiscoveryKademliaConfigFactory.Create(currentNode, [], new DiscoveryConfig { BucketSize = bucketSize, PingTimeout = pingTimeout, SendNodeTimeout = findNodeTimeout })
            : new KademliaConfig<Node> { CurrentNodeId = currentNode, KSize = bucketSize };
        if (realRouting)
        {
            routingTable = new KBucketTree<Node, ValueHash256>(config, new FromKeyNodeHashProvider<PublicKey, Node, ValueHash256>(new PublicKeyKeyOperator()), ValueHash256KademliaDistance.Instance);
            routingTable.TryAddOrRefresh(currentNode.Id.Hash.ValueHash256, currentNode, out _);
            IRoutingTable<Node, ValueHash256> actualRouting = routingTable;
            table.When(kad => kad.AddOrRefresh(Arg.Any<Node>())).Do(call => actualRouting.TryAddOrRefresh(call.Arg<Node>().Id.Hash.ValueHash256, call.Arg<Node>(), out _));
        }
        PeerAddressPolicy policy = new(ipResolver, new LocalAddressSource(LimboLogs.Instance, () => []));
        KademliaAdapter adapter = new(
            new Lazy<IKademlia<PublicKey, Node>>(table),
            routingTable,
            transport,
            packetCodec,
            nodeRecordProvider,
            new DiscoveryConfig { PingTimeout = pingTimeout, SendNodeTimeout = findNodeTimeout },
            config,
            new CryptoRandom(),
            ValueHash256KademliaDistance.Instance,
            logManager ?? LimboLogs.Instance,
            listenerState, policy)
        {
            ChallengeTimeProvider = timeProvider ?? TimeProvider.System,
            RequestTimeProvider = requestTimeProvider ?? TimeProvider.System,
            ChallengeLimiter = challengeLimiter,
            RecordSigner = recordSigner ?? new NodeRecordSigner(new Ecdsa())
        };

        return new TestPeer(adapter, transport, outbound, packetCodec, table, nodeRecordProvider, endpoint, policy, routingTable);
    }

    private static async Task PumpUntilComplete(Task task, TestPeer peerA, TestPeer peerB, CancellationToken token)
    {
        while (!task.IsCompleted)
        {
            Pump(peerA, peerB);
            Pump(peerB, peerA);
            await Task.Delay(10, token);
        }

        Pump(peerA, peerB);
        Pump(peerB, peerA);
    }

    private static async Task PumpUntil(Func<bool> condition, TestPeer peerA, TestPeer peerB, CancellationToken token)
    {
        while (!condition())
        {
            Pump(peerA, peerB);
            Pump(peerB, peerA);
            await Task.Delay(10, token);
        }

        Pump(peerA, peerB);
        Pump(peerB, peerA);
    }

    private static bool HasEnrSequence(Node node, ulong sequence)
    {
        if (node.Enr is null)
        {
            return false;
        }

        try
        {
            return node.Enr.EnrSequence == sequence;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasEnr(Node node) => node.Enr is not null;

    private static Node FindNode(IKademlia<PublicKey, Node> kademlia, PublicKey publicKey)
    {
        foreach (Node node in kademlia.IterateNodes())
        {
            if (node.Id.Equals(publicKey))
            {
                return node;
            }
        }

        throw new InvalidOperationException($"Node {publicKey} was not found in the routing table.");
    }

    private static bool HasReceivedNodeWithEnrSequence(IKademlia<PublicKey, Node> kademlia, PublicKey publicKey, ulong sequence)
    {
        foreach (NSubstitute.Core.ICall call in kademlia.ReceivedCalls())
        {
            if (call.GetMethodInfo().Name == nameof(IKademlia<,>.AddOrRefresh) &&
                call.GetArguments()[0] is Node node &&
                node.Id.Equals(publicKey) &&
                HasEnrSequence(node, sequence))
            {
                return true;
            }
        }

        return false;
    }

    private static Node CreateSignedNode(PrivateKey privateKey, IPEndPoint endpoint)
        => new(privateKey.PublicKey, endpoint)
        {
            Enr = TestEnrBuilder.BuildSigned(privateKey, endpoint.Address, tcpPort: endpoint.Port, udpPort: endpoint.Port)
        };

    private static void Pump(TestPeer from, TestPeer to)
    {
        while (from.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) datagram))
        {
            to.Transport.Receive(PooledUdpReceiveResult.Copy(datagram.Data, from.Endpoint));
        }
    }

    private sealed class BoundedDistanceKademlia(Hash256 currentNodeHash, int capacityPerDistance) : IKademlia<PublicKey, Node>
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, List<Node>> _nodesByDistance = [];
        private readonly Dictionary<int, List<Node>> _replacementsByDistance = [];

        public event EventHandler<Node>? OnNodeAdded;
        public event EventHandler<Node>? OnNodeRemoved;

        public void AddOrRefresh(Node node)
        {
            int distance = ValueHash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash.ValueHash256, node.Id.Hash.ValueHash256);
            bool added = false;
            lock (_lock)
            {
                List<Node> nodes = GetNodes(distance);
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].Id.Equals(node.Id))
                    {
                        nodes[i] = node;
                        return;
                    }
                }

                if (nodes.Count >= capacityPerDistance)
                {
                    return;
                }

                nodes.Add(node);
                added = true;
            }

            if (added)
            {
                OnNodeAdded?.Invoke(this, node);
            }
        }

        public bool TryGetNode(ValueHash256 nodeHash, out Node storedNode)
        {
            int distance = ValueHash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash.ValueHash256, nodeHash);
            lock (_lock)
            {
                if ((_nodesByDistance.TryGetValue(distance, out List<Node>? nodes) &&
                     TryGetNode(nodes, nodeHash, out storedNode)) ||
                    (_replacementsByDistance.TryGetValue(distance, out List<Node>? replacements) &&
                     TryGetNode(replacements, nodeHash, out storedNode)))
                {
                    return true;
                }
            }

            storedNode = null!;
            return false;
        }

        public void AddReplacement(Node node)
        {
            int distance = ValueHash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash.ValueHash256, node.Id.Hash.ValueHash256);
            lock (_lock)
            {
                GetReplacements(distance).Add(node);
            }
        }

        public void Remove(Node node)
        {
            int distance = ValueHash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash.ValueHash256, node.Id.Hash.ValueHash256);
            Node? removed = null;
            lock (_lock)
            {
                if (!_nodesByDistance.TryGetValue(distance, out List<Node>? nodes))
                {
                    return;
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!nodes[i].Id.Equals(node.Id))
                    {
                        continue;
                    }

                    removed = nodes[i];
                    nodes.RemoveAt(i);
                    PromoteReplacement(distance, nodes);
                    break;
                }
            }

            if (removed is not null)
            {
                OnNodeRemoved?.Invoke(this, removed);
            }
        }

        public bool Contains(PublicKey publicKey)
        {
            int distance = ValueHash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash.ValueHash256, publicKey.Hash.ValueHash256);
            lock (_lock)
            {
                if (!_nodesByDistance.TryGetValue(distance, out List<Node>? nodes))
                {
                    return false;
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].Id.Equals(publicKey))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public Task Run(CancellationToken token) => throw new NotSupportedException();

        public Task Bootstrap(CancellationToken token) => throw new NotSupportedException();

        public Task<Node[]> LookupNodesClosest(PublicKey key, CancellationToken token, int? k = null) => throw new NotSupportedException();

        public IAsyncEnumerable<Node> LookupNodes(PublicKey key, CancellationToken token, int? maxResults = null) => throw new NotSupportedException();

        public Node[] GetKNeighbour(PublicKey target, Node? excluding = null, bool excludeSelf = false) => throw new NotSupportedException();

        public Node[] GetAllAtDistance(int distance)
        {
            lock (_lock)
            {
                return _nodesByDistance.TryGetValue(distance, out List<Node>? nodes) ? nodes.ToArray() : [];
            }
        }

        public IEnumerable<Node> IterateNodes()
        {
            Node[] snapshot;
            lock (_lock)
            {
                List<Node> nodes = [];
                foreach (List<Node> bucketNodes in _nodesByDistance.Values)
                {
                    nodes.AddRange(bucketNodes);
                }

                snapshot = nodes.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                yield return snapshot[i];
            }
        }

        private List<Node> GetNodes(int distance)
        {
            if (!_nodesByDistance.TryGetValue(distance, out List<Node>? nodes))
            {
                nodes = [];
                _nodesByDistance[distance] = nodes;
            }

            return nodes;
        }

        private static bool TryGetNode(List<Node> nodes, ValueHash256 nodeHash, out Node storedNode)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Id.Hash == nodeHash)
                {
                    storedNode = nodes[i];
                    return true;
                }
            }

            storedNode = null!;
            return false;
        }

        private List<Node> GetReplacements(int distance)
        {
            if (!_replacementsByDistance.TryGetValue(distance, out List<Node>? replacements))
            {
                replacements = [];
                _replacementsByDistance[distance] = replacements;
            }

            return replacements;
        }

        private void PromoteReplacement(int distance, List<Node> nodes)
        {
            if (!_replacementsByDistance.TryGetValue(distance, out List<Node>? replacements) || replacements.Count == 0)
            {
                return;
            }

            Node replacement = replacements[0];
            replacements.RemoveAt(0);
            nodes.Add(replacement);
        }
    }

    private sealed record TestPeer(
        KademliaAdapter Adapter,
        DiscoveryV5Transport Transport,
        RecordingDatagramSocket Outbound,
        PacketCodec PacketCodec,
        IKademlia<PublicKey, Node> Kademlia,
        TestNodeRecordProvider NodeRecordProvider,
        IPEndPoint Endpoint,
        PeerAddressPolicy AddressPolicy,
        IRoutingTable<Node, ValueHash256> Routing) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Adapter.DisposeAsync();
            }
            finally
            {
                try
                {
                    Transport.Close();
                }
                finally
                {
                    PacketCodec.Dispose();
                    AddressPolicy.Dispose();
                }
            }
        }
    }

    private sealed class TestNodeRecordProvider(
        PrivateKey privateKey,
        IPEndPoint endpoint,
        bool includeEndpoint,
        ulong enrSequence,
        IPAddress? recordIp) : INodeRecordProvider
    {
        public NodeRecord Current { get; } = includeEndpoint
            ? TestEnrBuilder.BuildSigned(privateKey, recordIp ?? endpoint.Address, tcpPort: endpoint.Port, udpPort: endpoint.Port, enrSequence: enrSequence)
            : TestEnrBuilder.BuildSignedWithoutEndpoint(privateKey, enrSequence);

        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            return new ValueTask<NodeRecord>(Current);
        }
    }

    [Test]
    public async Task Ninth_distinct_distance_is_not_walked_or_returned(
        [Values(1, 8, 9)] int distinctCount, [Values] bool duplicate)
    {
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"));
        Node candidate = CreateSignedNode(TestItem.PrivateKeyC, IPEndPoint.Parse("127.0.0.1:10002"));
        recipient.Kademlia.GetAllAtDistance(Arg.Any<int>()).Returns([]);
        recipient.Kademlia.GetAllAtDistance(distinctCount).Returns([candidate]);
        int repetitions = duplicate ? 2 : 1;
        int[] distances = new int[distinctCount * repetitions];
        for (int index = 0; index < distances.Length; index++) distances[index] = index / repetitions + 1;
        using FindNodeMsg request = new([1], distances);
        byte[] challenge = await RequestChallenge(initiator, recipient, request, 1);
        byte[] handshake = EncodeHandshake(initiator, request, challenge, out Session session);
        using (session)
        {
            recipient.Kademlia.ClearReceivedCalls();
            await DeliverPacket(recipient, initiator.Endpoint, handshake);
            Packet packet;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) response), Is.True);
                Assert.That(initiator.PacketCodec.TryDecode(response.Data, out packet), Is.True);
            }
            using (packet)
            {
                Assert.That(PacketCodec.TryDecryptMessage(in packet, session.ReadKey, out Discv5Message message), Is.True);
                using (message)
                {
                    Assert.That(message, Is.TypeOf<NodesMsg>());
                    NodesMsg nodes = (NodesMsg)message;
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(nodes.Records, Has.Count.EqualTo(distinctCount <= 8 ? 1 : 0));
                        Assert.That(message.RequestId, Is.EqualTo(request.RequestId));
                        Assert.That(recipient.Outbound.Sent, Is.Empty);
                    }
                    if (distinctCount <= 8) Assert.That(nodes.Records[0].ContentHash, Is.EqualTo(candidate.Enr!.ContentHash));
                }
            }
        }
        for (int distance = 1; distance <= distinctCount; distance++)
            recipient.Kademlia.Received(distance <= 8 ? 1 : 0).GetAllAtDistance(distance);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Local_handshake_encode_refusal_is_neutral_and_next_peer_still_completes([Values] bool oversized, CancellationToken token)
    {
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"));
        SetPadding(initiator.NodeRecordProvider.Current, oversized ? 1400 : 0);
        Node receiver = new(TestItem.PublicKeyB, recipient.Endpoint);
        Task<bool> request = initiator.Adapter.Ping(receiver, token);
        Assert.That(initiator.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) ordinary), Is.True);
        await DeliverPacket(recipient, initiator.Endpoint, ordinary.Data);
        Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) challenge), Is.True);
        await DeliverPacket(initiator, recipient.Endpoint, challenge.Data);

        if (oversized)
        {
            await Assert.ThrowsAsync<KademliaRequestNotSentException>(async () => await request.WaitAsync(token));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(initiator.Outbound.Sent, Is.Empty);
                Assert.That(receiver.ValidatedProtocol, Is.Null);
            }
        }
        else
        {
            await PumpDirectUntilComplete(request, initiator, recipient, token);
            Assert.That(await request, Is.True);
        }

        SetPadding(initiator.NodeRecordProvider.Current, 0);
        await using TestPeer next = CreatePeer(TestItem.PrivateKeyC, IPEndPoint.Parse("127.0.0.1:10002"));
        Task<bool> nextRequest = initiator.Adapter.Ping(new Node(TestItem.PublicKeyC, next.Endpoint), token);
        await PumpDirectUntilComplete(nextRequest, initiator, next, token);
        Assert.That(await nextRequest, Is.True);
    }

    private static async Task PumpDirectUntilComplete(Task request, TestPeer initiator, TestPeer recipient, CancellationToken token)
    {
        while (!request.IsCompleted)
        {
            while (initiator.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) outgoing))
                await DeliverPacket(recipient, initiator.Endpoint, outgoing.Data);
            while (recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) incoming))
                await DeliverPacket(initiator, recipient.Endpoint, incoming.Data);
            await Task.Delay(1, token);
        }
    }

    private static void SetPadding(NodeRecord record, int size)
    {
        record.SetEntry(new PaddingEntry(size));
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyA).Sign(record);
    }

    private sealed class PaddingEntry(int size) : EnrContentEntry<byte[]>(new byte[size])
    {
        public override string Key => "padding";
        protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);
        protected override void EncodeValue(ref RlpWriter writer) => writer.Encode(Value);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Known_peer_ping_refreshes_its_record_before_endpoint_check_completes(CancellationToken token)
    {
        IPEndPoint endpoint = IPEndPoint.Parse("127.0.0.1:10000");
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, endpoint);
        Node known = new(TestItem.PublicKeyA, endpoint) { ValidatedProtocol = true };
        known.SetVerifiedEnr(initiator.NodeRecordProvider.Current);
        IRoutingTable<Node, ValueHash256> routing = Substitute.For<IRoutingTable<Node, ValueHash256>>();
        routing.TryGet(TestItem.PublicKeyA.Hash.ValueHash256, out _).Returns(call =>
        {
            call[1] = known;
            return true;
        });
        IKademlia<PublicKey, Node> table = Substitute.For<IKademlia<PublicKey, Node>>();
        TaskCompletionSource refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        table.When(t => t.AddOrRefresh(Arg.Is<Node>(n => n.Enr != null && n.Enr.EnrSequence == 2)))
            .Do(_ => refreshed.TrySetResult());
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"), kademlia: table, routing: routing);
        using CancellationTokenSource checks = CancellationTokenSource.CreateLinkedTokenSource(token);
        using PingMsg ping = new(new RequestId(1, 1), 2);
        byte[] challenge = await RequestChallenge(initiator, recipient, ping, 1);
        byte[] handshake = EncodeHandshake(initiator, ping, challenge, out Session session);
        using (session)
        {
            try
            {
                await DeliverPacket(recipient, endpoint, handshake, checks.Token);
                (int endpointChecks, int refreshes, RequestId requestId) = CountEndpointResponses(initiator, recipient, session);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(refreshes, Is.EqualTo(1));
                    Assert.That(endpointChecks, Is.EqualTo(1));
                }
                NodeRecord newer = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, endpoint.Address,
                    tcpPort: endpoint.Port, udpPort: endpoint.Port, enrSequence: 2);
                using NodesMsg nodes = new(requestId, 1, [newer]);
                await DeliverPacket(recipient, endpoint, initiator.PacketCodec.EncodeOrdinary(TestItem.PublicKeyB,
                    session.WriteKey, nodes, new byte[PacketCodec.NonceSize]), checks.Token);
                await refreshed.Task.WaitAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(known.Enr!.EnrSequence, Is.EqualTo(2));
                    Assert.That(known.IsVerifiedEnr(known.Enr), Is.True);
                }
            }
            finally
            {
                await checks.CancelAsync();
            }
        }
    }

    [TestCase(1, 300, 0, 4)]
    [TestCase(0, 300, 0, 256)]
    [TestCase(0, 4, 0, 4)]
    [TestCase(7, 2240, 0, 28)]
    [TestCase(7, 7, 320, 7)]
    [CancelAfter(60000)]
    public async Task Silent_handshakes_leave_capacity_for_honest_lookups(int sources, int handshakeCount,
        int ordinaryPings, int expectedProbes, CancellationToken token)
    {
        using CancellationTokenSource checks = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("[::1]:10001"),
            pingTimeout: 60000, findNodeTimeout: 60000,
            challengeLimiter: new AddressBurstLimiter(10000, 10000, TimeSpan.FromMinutes(1)));
        List<Task<Node[]?>> lookups = [];
        try
        {
            int probes = 0;
            int refreshes = 0;
            for (int index = 0; index < handshakeCount; index++)
            {
                byte[] keyBytes = new byte[32];
                BinaryPrimitives.WriteInt32BigEndian(keyBytes.AsSpan(28), index + 1000);
                using PrivateKey key = new(keyBytes);
                int prefix = sources == 0 ? index + 1 : index % sources + 1;
                IPEndPoint endpoint = new(IPAddress.Parse($"2001:4860:1:{prefix:x}::{index + 1:x}"), 30303);
                await using TestPeer initiator = CreatePeer(key, endpoint);
                using PingMsg ping = new(new RequestId(1, 1), ordinaryPings == 0 ? 2UL : 1UL);
                byte[] challenge = await RequestChallenge(initiator, recipient, ping, 1);
                byte[] handshake = EncodeHandshake(initiator, ping, challenge, out Session session);
                using (session)
                {
                    await DeliverPacket(recipient, endpoint, handshake, checks.Token);
                    (int Probes, int Refreshes, RequestId RefreshRequestId) counts = CountEndpointResponses(initiator, recipient, session);
                    probes += counts.Probes;
                    refreshes += counts.Refreshes;
                    for (int pingIndex = 0; pingIndex < ordinaryPings; pingIndex++)
                    {
                        using PingMsg next = new(new RequestId((ulong)pingIndex + 2, 2), (ulong)pingIndex + 2);
                        byte[] nonce = new byte[PacketCodec.NonceSize];
                        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(8), pingIndex + 2);
                        await DeliverPacket(recipient, endpoint, initiator.PacketCodec.EncodeOrdinary(TestItem.PublicKeyB,
                            session.WriteKey, next, nonce), checks.Token);
                        counts = CountEndpointResponses(initiator, recipient, session);
                        probes += counts.Probes;
                        refreshes += counts.Refreshes;
                    }
                }
            }

            recipient.Kademlia.DidNotReceive().AddOrRefresh(Arg.Any<Node>());

            await using TestPeer honest = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("[::1]:10000"));
            using TalkReqMsg talk = new(new RequestId(2, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
            byte[] honestChallenge = await RequestChallenge(honest, recipient, talk, 2);
            byte[] honestHandshake = EncodeHandshake(honest, talk, honestChallenge, out Session honestSession);
            using (honestSession)
            {
                await DeliverPacket(recipient, honest.Endpoint, honestHandshake, checks.Token);
                AssertTalkResponse(honest, recipient, honestSession, talk.RequestId);
                for (int index = 0; index < 768; index++)
                    lookups.Add(recipient.Adapter.FindNeighbours(new Node(TestItem.PublicKeyA, honest.Endpoint), TestItem.PublicKeyC, checks.Token));

                int admittedLookups = recipient.Outbound.Sent.Count;
                bool lookupFaulted = lookups.Any(task => task.IsFaulted);
                Packet packet;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) sent), Is.True);
                    Assert.That(honest.PacketCodec.TryDecode(sent.Data, out packet), Is.True);
                }
                RequestId requestId;
                using (packet)
                {
                    Assert.That(PacketCodec.TryDecryptMessage(in packet, honestSession.ReadKey, out Discv5Message request), Is.True);
                    using (request)
                    {
                        Assert.That(request, Is.TypeOf<FindNodeMsg>());
                        requestId = request.RequestId;
                    }
                }

                using NodesMsg nodes = new(requestId, 1, []);
                await DeliverPacket(recipient, honest.Endpoint, honest.PacketCodec.EncodeOrdinary(TestItem.PublicKeyB,
                    honestSession.WriteKey, nodes, new byte[PacketCodec.NonceSize]), checks.Token);
                Node[]? result = await lookups[0].WaitAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(probes, Is.EqualTo(expectedProbes));
                    Assert.That(refreshes, Is.Zero);
                    Assert.That(admittedLookups, Is.EqualTo(768));
                    Assert.That(lookupFaulted, Is.False);
                    Assert.That(result, Is.Empty);
                }
            }
        }
        finally
        {
            await checks.CancelAsync();
            await ((Task)Task.WhenAll(lookups)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(60000)]
    public async Task Known_identity_sequence_flood_ends_after_two_refresh_requests([Values(1, 7)] int identities, CancellationToken token)
    {
        ManualTimeProvider clock = new();
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("8.8.8.8:30303"),
            requestTimeProvider: clock, findNodeTimeout: 500, realRouting: true);
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        List<PrivateKey> keys = [];
        List<TestPeer> peers = [];
        List<RefreshWire> wires = [];
        List<Task<bool>> cohorts = [];
        List<Task<Node[]?>> lookups = [];
        try
        {
            for (int identity = 0; identity < identities; identity++)
            {
                PrivateKey key = RefreshKey(identity);
                keys.Add(key);
                TestPeer remote = CreatePeer(key, new IPEndPoint(IPAddress.Parse($"8.{identity + 1}.4.1"), 30303));
                peers.Add(remote);
                RefreshWire wire = await ProveRefreshPeer(remote, recipient, cleanup.Token);
                wires.Add(wire);
                Task<bool> cohort = recipient.Adapter.Ping(wire.Node, cleanup.Token);
                cohorts.Add(cohort);
                await wire.ReplyToPing(2, cleanup.Token);
                await wire.Requests[0].Task.WaitAsync(token);
            }
            for (int phase = 0; phase < 2; phase++)
            {
                foreach (RefreshWire wire in wires)
                {
                    for (int bump = 0; bump < 160; bump++)
                    {
                        using PingMsg ping = new(new RequestId((ulong)(phase * 160 + bump + 100), 7), (ulong)(phase * 160 + bump + 3));
                        await wire.Send(ping, cleanup.Token);
                    }
                }
                Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(identities));
                if (phase == 1)
                {
                    recipient.Outbound.Sent.Clear();
                    for (int lookup = 0; lookup < 768; lookup++)
                        lookups.Add(recipient.Adapter.FindNeighbours(wires[0].Node, TestItem.PublicKeyC, cleanup.Token));
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(lookups, Has.All.Matches<Task<Node[]?>>(lookup => lookup is { IsCompleted: false }));
                        Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(identities + 768));
                    }
                    Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) first), Is.True);
                    using NodesMsg matched = new(wires[0].DecodeRequestId(first.Data), 1, []);
                    await wires[0].Send(matched, cleanup.Token);
                    Assert.That(await lookups[0].WaitAsync(token), Is.Empty);
                }
                clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(500));
                for (int identity = 0; identity < identities; identity++)
                    await Task.WhenAny(cohorts[identity], wires[identity].Requests[phase + 1].Task).WaitAsync(token);
            }
            for (int identity = 0; identity < identities; identity++)
            {
                RefreshWire wire = wires[identity];
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(wire.RefreshCount, Is.EqualTo(2), "A third refresh must fail the dispatch bound before cleanup.");
                    Assert.That(await cohorts[identity].WaitAsync(token), Is.True);
                }
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(wire.Node.RequestingEnrSequence, Is.Zero);
                    Assert.That(wire.Node.HighestObservedEnrSequence, Is.EqualTo(1));
                }
                List<Node> slots = [];
                try
                {
                    for (int slot = 0; slot < 4; slot++)
                    {
                        Node endpoint = new(keys[identity].PublicKey, peers[identity].Endpoint.Address.ToString(), 30400 + slot);
                        Assert.That(recipient.Adapter.TryReserveEndpointCheck(endpoint), Is.True);
                        slots.Add(endpoint);
                    }
                }
                finally { foreach (Node endpoint in slots) recipient.Adapter.ReleaseEndpointCheck(endpoint); }
            }
            await Task.WhenAll(lookups).WaitAsync(token);
            Assert.That(recipient.Adapter.ResponseHandlerCount, Is.Zero);
        }
        finally
        {
            await cleanup.CancelAsync();
            await Task.WhenAll(cohorts.Cast<Task>().Concat(lookups)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (RefreshWire wire in wires) wire.Dispose();
            foreach (TestPeer peer in peers) await peer.DisposeAsync();
            foreach (PrivateKey key in keys) key.Dispose();
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Coalesced_sequence_after_first_timeout_stores_the_second_signed_record(CancellationToken token)
    {
        ManualTimeProvider clock = new();
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("8.8.8.8:30303"), requestTimeProvider: clock, findNodeTimeout: 500, realRouting: true);
        await using TestPeer remote = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("8.8.4.4:30303"));
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        using RefreshWire wire = await ProveRefreshPeer(remote, recipient, cleanup.Token);
        Task<bool> cohort = recipient.Adapter.Ping(wire.Node, cleanup.Token);
        try
        {
            await wire.ReplyToPing(2, cleanup.Token);
            await wire.Requests[0].Task.WaitAsync(token);
            using PingMsg advertised = new(new RequestId(100, 7), 3);
            await wire.Send(advertised, cleanup.Token);
            clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(500));
            await Task.WhenAny(cohort, wire.Requests[1].Task).WaitAsync(token);
            if (wire.Requests[1].Task.IsCompleted)
            {
                NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, remote.Endpoint.Address, tcpPort: remote.Endpoint.Port, udpPort: remote.Endpoint.Port, enrSequence: 3);
                using NodesMsg response = new(await wire.Requests[1].Task, 1, [record]);
                await wire.Send(response, cleanup.Token);
            }
            await cohort.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(wire.Node.HighestObservedEnrSequence, Is.EqualTo(3));
                Assert.That(recipient.Routing.TryGet(TestItem.PublicKeyA.Hash.ValueHash256, out Node? stored) && stored.Enr?.EnrSequence == 3, Is.True);
                Assert.That(recipient.Adapter.ResponseHandlerCount, Is.Zero);
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)cohort).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Dispatched_sequence_two_remains_valid_after_advertisement_three(CancellationToken token)
    {
        ManualTimeProvider clock = new();
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("8.8.8.8:30303"), requestTimeProvider: clock, findNodeTimeout: 500, realRouting: true);
        await using TestPeer remote = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("8.8.4.4:30303"));
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        using RefreshWire wire = await ProveRefreshPeer(remote, recipient, cleanup.Token);
        Task<bool> cohort = recipient.Adapter.Ping(wire.Node, cleanup.Token);
        try
        {
            await wire.ReplyToPing(2, cleanup.Token);
            await wire.Requests[0].Task.WaitAsync(token);
            using PingMsg advertised = new(new RequestId(100, 7), 3);
            await wire.Send(advertised, cleanup.Token);
            NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, remote.Endpoint.Address, tcpPort: remote.Endpoint.Port, udpPort: remote.Endpoint.Port, enrSequence: 2);
            using NodesMsg response = new(await wire.Requests[0].Task, 1, [record]);
            await wire.Send(response, cleanup.Token);
            await Task.WhenAny(cohort, wire.Requests[1].Task).WaitAsync(token);
            if (!cohort.IsCompleted) clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(500));
            await cohort.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(wire.Node.HighestObservedEnrSequence, Is.EqualTo(2));
                Assert.That(recipient.Routing.TryGet(TestItem.PublicKeyA.Hash.ValueHash256, out Node? stored) && stored.Enr?.EnrSequence == 2, Is.True);
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)cohort).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(60000)]
    public Task Mixed_automatic_work_preserves_all_seven_hundred_sixty_eight_lookup_slots(CancellationToken token)
        => MixedAutomaticWork(false, token);

    [Test]
    [CancelAfter(60000)]
    public Task Endpoint_refresh_handoff_uses_the_released_shared_slot(CancellationToken token)
        => MixedAutomaticWork(true, token);

    private static async Task MixedAutomaticWork(bool handoff, CancellationToken token)
    {
        ManualTimeProvider clock = new();
        ILogManager logs = EndpointCompletionLog(128, out Task endpointCompletion, out Task endpointCohortCompleted);
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("8.8.8.8:30303"), requestTimeProvider: clock,
            pingTimeout: 60000, findNodeTimeout: 60000, realRouting: true, challengeLimiter: new AddressBurstLimiter(10000, 10000, TimeSpan.FromMinutes(1)), logManager: logs);
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        List<TestPeer> peers = [];
        List<PrivateKey> keys = [];
        List<RefreshWire> wires = [];
        List<Task> requests = [];
        bool fullPopulation = false;
        try
        {
            for (int source = 0; source < 64; source++)
            {
                for (int kind = 0; kind < 4; kind++)
                {
                    PrivateKey key = RefreshKey(1000 + source * 4 + kind);
                    keys.Add(key);
                    TestPeer remote = CreatePeer(key, new IPEndPoint(IPAddress.Parse($"11.{source + 1}.0.1"), 30303 + kind));
                    peers.Add(remote);
                    if (kind < 2)
                    {
                        RefreshWire wire = await ProveRefreshPeer(remote, recipient, cleanup.Token);
                        wires.Add(wire);
                        Task<bool> refresh = recipient.Adapter.Ping(wire.Node, cleanup.Token);
                        requests.Add(refresh);
                        await wire.ReplyToPing(2, cleanup.Token);
                        await Task.WhenAny(refresh, wire.Requests[0].Task).WaitAsync(token);
                        Assert.That(wire.RefreshCount, Is.EqualTo(1));
                    }
                    else
                    {
                        using PingMsg ping = new(new RequestId(1, 1), 1);
                        byte[] challenge = await RequestChallenge(remote, recipient, ping, 1);
                        byte[] handshake = EncodeHandshake(remote, ping, challenge, out Session session);
                        RefreshWire wire = new(remote, recipient, session, new Node(key.PublicKey, remote.Endpoint));
                        wires.Add(wire);
                        await DeliverPacket(recipient, remote.Endpoint, handshake, cleanup.Token);
                        (int probes, int refreshes, _) = CountEndpointResponses(remote, recipient, session);
                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(probes, Is.EqualTo(1));
                            Assert.That(refreshes, Is.Zero);
                        }
                    }
                }
            }
            fullPopulation = true;
            Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(256));
            if (handoff)
            {
                await wires[2].ReplyToPing(2, cleanup.Token);
                await Task.WhenAny(wires[2].Requests[0].Task, endpointCohortCompleted).WaitAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(wires[2].RefreshCount, Is.EqualTo(1));
                    Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(256));
                }
            }
            using PrivateKey extraKey = RefreshKey(2000);
            await using TestPeer extra = CreatePeer(extraKey, IPEndPoint.Parse("13.1.0.1:30303"));
            using RefreshWire extraWire = await ProveRefreshPeer(extra, recipient, cleanup.Token);
            using PingMsg inbound = new(new RequestId(100, 7), 2);
            await extraWire.Send(inbound, cleanup.Token);
            Task<bool> pongPath = recipient.Adapter.Ping(extraWire.Node, cleanup.Token);
            requests.Add(pongPath);
            await extraWire.ReplyToPing(2, cleanup.Token);
            await Task.WhenAny(pongPath, extraWire.Requests[0].Task).WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(extraWire.RefreshCount, Is.Zero, "A refused PONG refresh must not dispatch before cohort completion.");
                Assert.That(await pongPath.WaitAsync(token), Is.True);
            }
            using PrivateKey endpointKey = RefreshKey(2001);
            await using TestPeer endpoint = CreatePeer(endpointKey, IPEndPoint.Parse("14.1.0.1:30303"));
            using PingMsg endpointPing = new(new RequestId(1, 1), 1);
            byte[] endpointChallenge = await RequestChallenge(endpoint, recipient, endpointPing, 1);
            byte[] endpointHandshake = EncodeHandshake(endpoint, endpointPing, endpointChallenge, out Session endpointSession);
            using (endpointSession)
            {
                await DeliverPacket(recipient, endpoint.Endpoint, endpointHandshake, cleanup.Token);
                (int probes, int refreshes, _) = CountEndpointResponses(endpoint, recipient, endpointSession);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(probes, Is.Zero);
                    Assert.That(refreshes, Is.Zero);
                    Assert.That(extraWire.RefreshCount, Is.Zero);
                    Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(256));
                }
            }
            recipient.Outbound.Sent.Clear();
            List<Task<Node[]?>> lookups = [];
            for (int index = 0; index < 768; index++)
            {
                Task<Node[]?> lookup = recipient.Adapter.FindNeighbours(extraWire.Node, TestItem.PublicKeyC, cleanup.Token);
                lookups.Add(lookup);
                requests.Add(lookup);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lookups, Has.All.Matches<Task<Node[]?>>(lookup => lookup is { IsCompleted: false }));
                Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(1024));
            }
            Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) first), Is.True);
            using NodesMsg matched = new(extraWire.DecodeRequestId(first.Data), 1, []);
            await extraWire.Send(matched, cleanup.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await lookups[0].WaitAsync(token), Is.Empty);
                Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(1023));
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await Task.WhenAll(requests).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (fullPopulation) await endpointCompletion.WaitAsync(token);
            foreach (RefreshWire wire in wires) wire.Dispose();
            foreach (TestPeer peer in peers) await peer.DisposeAsync();
            foreach (PrivateKey key in keys) key.Dispose();
        }
    }

    /// <remarks>One authenticated identity at four UDP ports can fill one source allowance without routing admission.</remarks>
    [Test]
    [CancelAfter(10000)]
    public async Task accepted_residual_same_identity_four_ports_fill_one_endpoint_source(CancellationToken token)
    {
        ManualTimeProvider clock = new();
        ILogManager logs = EndpointCompletionLog(4, out Task completion);
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("8.8.8.8:30303"), realRouting: true, logManager: logs, requestTimeProvider: clock);
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            for (int port = 0; port < 5; port++)
            {
                await using TestPeer remote = CreatePeer(TestItem.PrivateKeyA, new IPEndPoint(IPAddress.Parse("8.8.4.4"), 30303 + port));
                using PingMsg ping = new(new RequestId(1, 1), 1);
                byte[] challenge = await RequestChallenge(remote, recipient, ping, 1);
                byte[] handshake = EncodeHandshake(remote, ping, challenge, out Session session);
                using (session)
                {
                    await DeliverPacket(recipient, remote.Endpoint, handshake, cleanup.Token);
                    (int probes, int refreshes, _) = CountEndpointResponses(remote, recipient, session);
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(probes, Is.EqualTo(port < 4 ? 1 : 0));
                        Assert.That(refreshes, Is.Zero);
                    }
                }
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(4));
                Assert.That(recipient.Routing.TryGet(TestItem.PublicKeyA.Hash.ValueHash256, out _), Is.False);
            }
            await cleanup.CancelAsync();
            await completion.WaitAsync(token);
            Node later = new(TestItem.PublicKeyA, "8.8.4.4", 30400);
            Assert.That(recipient.Adapter.TryReserveEndpointCheck(later), Is.True);
            recipient.Adapter.ReleaseEndpointCheck(later);
        }
        finally
        {
            await cleanup.CancelAsync();
        }
    }

    [Test]
    [CancelAfter(60000)]
    public async Task Fifth_known_refresh_at_one_source_remains_locally_unassigned([Values] bool inbound, CancellationToken token)
    {
        ManualTimeProvider clock = new();
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("8.8.8.8:30303"), realRouting: true, requestTimeProvider: clock, findNodeTimeout: 60000, challengeLimiter: new AddressBurstLimiter(10000, 10000, TimeSpan.FromMinutes(1)));
        for (int filler = 0; filler < 32; filler++)
        {
            using PrivateKey key = RefreshKey(3000 + filler);
            await using TestPeer remote = CreatePeer(key, new IPEndPoint(IPAddress.Parse($"20.{filler + 1}.0.1"), 30303));
            using RefreshWire wire = await ProveRefreshPeer(remote, recipient, token);
            recipient.Outbound.Sent.Clear();
        }
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        List<PrivateKey> keys = [];
        List<TestPeer> peers = [];
        List<RefreshWire> wires = [];
        List<CancellationTokenSource> cohorts = [];
        List<Task<bool>> requests = [];
        try
        {
            for (int candidate = 0; wires.Count < 5 && candidate < 1024; candidate++)
            {
                PrivateKey key = RefreshKey(3100 + candidate);
                keys.Add(key);
                TestPeer remote = CreatePeer(key, new IPEndPoint(IPAddress.Parse("21.1.0.1"), 30303 + candidate));
                peers.Add(remote);
                RefreshWire wire = await ProveRefreshPeer(remote, recipient, cleanup.Token, requireKnown: false);
                if (!recipient.Routing.TryGet(key.PublicKey.Hash.ValueHash256, out _)) { wire.Dispose(); continue; }
                wires.Add(wire);
                if (wires.Count <= 4)
                {
                    CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cleanup.Token);
                    cohorts.Add(cancellation);
                    Task<bool> refresh = recipient.Adapter.Ping(wire.Node, cancellation.Token);
                    requests.Add(refresh);
                    await wire.ReplyToPing(2, cancellation.Token);
                    await Task.WhenAny(refresh, wire.Requests[0].Task).WaitAsync(token);
                    Assert.That(wire.RefreshCount, Is.EqualTo(1));
                }
            }
            Assert.That(wires, Has.Count.EqualTo(5), "Production routing must admit five verified identities in suitable buckets.");
            RefreshWire fifth = wires[4];
            Task<bool>? refused = null;
            if (inbound)
            {
                using PingMsg ping = new(new RequestId(100, 7), 2);
                await fifth.Send(ping, cleanup.Token);
            }
            else
            {
                refused = recipient.Adapter.Ping(fifth.Node, cleanup.Token);
                requests.Add(refused);
                await fifth.ReplyToPing(2, cleanup.Token);
                await Task.WhenAny(refused, fifth.Requests[0].Task).WaitAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(fifth.RefreshCount, Is.Zero, "PONG completion must refuse the fifth refresh before waiting for the cohort.");
                    Assert.That(await refused.WaitAsync(token), Is.True);
                }
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(fifth.RefreshCount, Is.Zero);
                Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(4));
                Assert.That(fifth.Node.RequestingEnrSequence, Is.Zero);
            }
            await cohorts[0].CancelAsync();
            await ((Task)requests[0]).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            fifth.PingRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> admitted = recipient.Adapter.Ping(fifth.Node, cleanup.Token);
            requests.Add(admitted);
            await fifth.ReplyToPing(2, cleanup.Token);
            await Task.WhenAny(admitted, fifth.Requests[0].Task).WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(fifth.RefreshCount, Is.EqualTo(1));
                Assert.That(recipient.Adapter.ResponseHandlerCount, Is.EqualTo(4));
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)Task.WhenAll(requests)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (CancellationTokenSource cancellation in cohorts) cancellation.Dispose();
            foreach (RefreshWire wire in wires) wire.Dispose();
            foreach (TestPeer peer in peers) await peer.DisposeAsync();
            foreach (PrivateKey key in keys) key.Dispose();
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Refresh_with_a_mismatched_record_releases_its_original_source(CancellationToken token)
    {
        ManualTimeProvider clock = new();
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("8.8.8.8:30303"), realRouting: true, requestTimeProvider: clock);
        await using TestPeer remote = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("8.8.4.4:30303"));
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        using RefreshWire wire = await ProveRefreshPeer(remote, recipient, cleanup.Token);
        List<Node> reserved = [];
        Task<bool>? cohort = null;
        try
        {
            for (int index = 0; index < 3; index++)
            {
                Node endpoint = new(TestItem.PublicKeyC, "8.8.4.4", 30303 + index);
                Assert.That(recipient.Adapter.TryReserveEndpointCheck(endpoint), Is.True);
                reserved.Add(endpoint);
            }
            cohort = recipient.Adapter.Ping(wire.Node, cleanup.Token);
            await wire.ReplyToPing(2, cleanup.Token);
            await wire.Requests[0].Task.WaitAsync(token);
            NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, IPAddress.Parse("8.8.4.5"), tcpPort: 30400, udpPort: 30400, enrSequence: 2);
            using NodesMsg response = new(await wire.Requests[0].Task, 1, [record]);
            Node next = new(TestItem.PublicKeyC, "8.8.4.4", 30500);
            Assert.That(recipient.Adapter.TryReserveEndpointCheck(next), Is.False);
            await wire.Send(response, cleanup.Token);
            bool answered = await cohort.WaitAsync(token);
            recipient.Routing.TryGet(wire.Node.Id.Hash.ValueHash256, out Node? stored);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(answered, Is.True);
                Assert.That(stored?.DiscoveryAddress, Is.EqualTo(remote.Endpoint));
                Assert.That(recipient.Adapter.TryReserveEndpointCheck(next), Is.True);
            }
            reserved.Add(next);
        }
        finally
        {
            await cleanup.CancelAsync();
            if (cohort is not null) await ((Task)cohort).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (Node endpoint in reserved) recipient.Adapter.ReleaseEndpointCheck(endpoint);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Same_identity_endpoint_churn_waits_for_refresh_cooldown([Values("port", "address")] string change, CancellationToken token)
    {
        ManualTimeProvider clock = new();
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("8.8.8.8:30303"), requestTimeProvider: clock, realRouting: true);
        await using TestPeer original = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("8.8.4.4:30303"));
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        using (RefreshWire first = await ProveRefreshPeer(original, recipient, cleanup.Token))
        {
            Task<bool> refresh = recipient.Adapter.Ping(first.Node, cleanup.Token);
            await first.ReplyToPing(2, cleanup.Token);
            await CompleteSignedRefresh(first, TestItem.PrivateKeyA, original.Endpoint, 2, refresh, cleanup.Token);
        }
        await using TestPeer alias = CreatePeer(TestItem.PrivateKeyA, new IPEndPoint(
            IPAddress.Parse(change == "address" ? "8.8.4.5" : "8.8.4.4"), change == "port" ? 30304 : 30303));
        using RefreshWire wire = await ProveRefreshPeer(alias, recipient, cleanup.Token);
        Task<bool> refused = recipient.Adapter.Ping(wire.Node, cleanup.Token);
        try
        {
            await wire.ReplyToPing(3, cleanup.Token);
            await Task.WhenAny(refused, wire.Requests[0].Task).WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(wire.RefreshCount, Is.Zero, "Changing a port or address must not bypass the identity cooldown.");
                Assert.That(await refused.WaitAsync(token), Is.True);
            }
            clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(60));
            wire.PingRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> recovered = recipient.Adapter.Ping(wire.Node, cleanup.Token);
            refused = recovered;
            await wire.ReplyToPing(3, cleanup.Token);
            await CompleteSignedRefresh(wire, TestItem.PrivateKeyA, alias.Endpoint, 3, recovered, cleanup.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(wire.RefreshCount, Is.EqualTo(1));
                Assert.That(wire.Node.HighestObservedEnrSequence, Is.EqualTo(3));
                Assert.That(clock.GetTimestamp(), Is.EqualTo(TimeSpan.FromSeconds(60).Ticks));
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)refused).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task CompleteSignedRefresh(RefreshWire wire, PrivateKey key, IPEndPoint endpoint, ulong sequence, Task<bool> cohort, CancellationToken token)
    {
        await Task.WhenAny(cohort, wire.Requests[0].Task).WaitAsync(token);
        if (wire.Requests[0].Task.IsCompleted)
        {
            NodeRecord record = TestEnrBuilder.BuildSigned(key, endpoint.Address, tcpPort: endpoint.Port, udpPort: endpoint.Port, enrSequence: sequence);
            using NodesMsg response = new(await wire.Requests[0].Task, 1, [record]);
            await wire.Send(response, token);
        }
        Assert.That(await cohort.WaitAsync(token), Is.True);
    }

    [Test]
    public void Automatic_work_leases_release_exactly_once_without_changing_other_sources()
    {
        EndpointCheckLimiter limiter = new(8);
        Node refresh = new(TestItem.PublicKeyA, "8.8.4.4", 30303);
        Assert.That(limiter.TryReserveRefresh(refresh, out IDisposable? owner), Is.True);
        List<IDisposable> leases = [];
        bool released = false;
        try
        {
            for (int port = 0; port < 4; port++)
            {
                Assert.That(limiter.TryReserveEndpoint(new Node(TestItem.PublicKeyB, "8.8.4.5", 30303 + port), out IDisposable? lease), Is.True);
                leases.Add(lease!);
            }
            owner!.Dispose();
            released = true;
            Assert.That(() => owner.Dispose(), Throws.Nothing, "Repeated release must be idempotent.");
            Assert.That(limiter.TryReserveEndpoint(new Node(TestItem.PublicKeyB, "8.8.4.5", 30400), out _), Is.False);
            for (int port = 0; port < 4; port++)
            {
                Assert.That(limiter.TryReserveEndpoint(new Node(TestItem.PublicKeyA, "8.8.4.4", 30400 + port), out IDisposable? lease), Is.True);
                leases.Add(lease!);
            }
        }
        finally
        {
            if (!released) owner?.Dispose();
            foreach (IDisposable lease in leases) lease.Dispose();
        }
    }

    [Test]
    public void Independent_aliases_do_not_reserve_another_refresh_after_cooldown_while_one_is_active()
    {
        ManualTimeProvider clock = new();
        EndpointCheckLimiter limiter = new(8) { TimeProvider = clock };
        Node first = new(TestItem.PublicKeyA, "8.8.4.4", 30303);
        Node alias = new(TestItem.PublicKeyA, "8.8.4.5", 30304);
        Assert.That(limiter.TryReserveRefresh(first, out IDisposable? owner), Is.True);
        using (owner)
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            Assert.That(limiter.TryReserveRefresh(alias, out _), Is.False);
            owner!.Dispose();
            Assert.That(limiter.TryReserveRefresh(alias, out IDisposable? next), Is.True);
            next!.Dispose();
        }
    }

    private static ILogManager EndpointCompletionLog(int expected, out Task completion)
        => EndpointCompletionLog(expected, out completion, out _);

    private static ILogManager EndpointCompletionLog(int expected, out Task completion, out Task cohortCompleted)
    {
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsTrace.Returns(true);
        logger.When(log => log.Trace(Arg.Is<string>(message => message.StartsWith("Discv5 endpoint check released", StringComparison.Ordinal))))
            .Do(_ => { if (Interlocked.Increment(ref count) == expected) released.TrySetResult(); });
        logger.When(log => log.Trace(Arg.Is<string>(message => message.StartsWith("Discv5 automatic endpoint cohort completed", StringComparison.Ordinal))))
            .Do(_ => ended.TrySetResult());
        ILogManager manager = new OneLoggerLogManager(new ILogger(logger));
        Assert.That(manager.GetClassLogger<KademliaAdapter>().IsTrace, Is.True);
        completion = released.Task;
        cohortCompleted = ended.Task;
        return manager;
    }

    private static PrivateKey RefreshKey(int index)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), index + 10000);
        return new(bytes);
    }

    private static async Task<RefreshWire> ProveRefreshPeer(TestPeer remote, TestPeer recipient, CancellationToken token, bool requireKnown = true)
    {
        using TalkReqMsg talk = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] challenge = await RequestChallenge(remote, recipient, talk, 1);
        byte[] handshake = EncodeHandshake(remote, talk, challenge, out Session session);
        try
        {
            await DeliverPacket(recipient, remote.Endpoint, handshake, token);
            CountEndpointResponses(remote, recipient, session);
            Node node = new(remote.NodeRecordProvider.Current.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)!.Decompress(), remote.Endpoint);
            node.SetVerifiedEnr(remote.NodeRecordProvider.Current);
            RefreshWire wire = new(remote, recipient, session, node);
            Task<bool> proof = recipient.Adapter.Ping(node, token);
            await wire.ReplyToPing(1, token);
            Assert.That(await proof.WaitAsync(token), Is.True);
            if (requireKnown) Assert.That(recipient.Routing.TryGet(node.Id.Hash.ValueHash256, out _), Is.True);
            wire.PingRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return wire;
        }
        catch
        {
            recipient.Outbound.OnSend = null;
            session.Dispose();
            throw;
        }
    }

    private sealed class RefreshWire : IDisposable
    {
        private readonly TestPeer _remote;
        private readonly TestPeer _recipient;
        private readonly Session _session;
        private long _nonce;
        internal Node Node { get; }
        internal TaskCompletionSource<RequestId> PingRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RequestId>[] Requests { get; } = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<RequestId>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        internal int RefreshCount;

        internal RefreshWire(TestPeer remote, TestPeer recipient, Session session, Node node)
        {
            _remote = remote;
            _recipient = recipient;
            _session = session;
            Node = node;
            recipient.Outbound.OnSend += Capture;
        }

        private void Capture(byte[] bytes, IPEndPoint destination)
        {
            if (!destination.Equals(_remote.Endpoint)) return;
            Assert.That(_remote.PacketCodec.TryDecode(bytes, out Packet packet), Is.True);
            using (packet)
            {
                Assert.That(PacketCodec.TryDecryptMessage(in packet, _session.ReadKey, out Discv5Message message), Is.True);
                using (message)
                {
                    if (message is PingMsg) PingRequest.TrySetResult(message.RequestId);
                    if (message is FindNodeMsg find && find.Distances.Count == 1 && find.Distances[0] == 0)
                        Requests[Math.Min(Interlocked.Increment(ref RefreshCount) - 1, 2)].TrySetResult(message.RequestId);
                }
            }
        }

        internal async Task ReplyToPing(ulong sequence, CancellationToken token)
        {
            using PongMsg pong = new(await PingRequest.Task.WaitAsync(token), sequence, _recipient.Endpoint.Address, _recipient.Endpoint.Port);
            await Send(pong, token);
        }

        internal RequestId DecodeRequestId(byte[] bytes)
        {
            Assert.That(_remote.PacketCodec.TryDecode(bytes, out Packet packet), Is.True);
            using (packet)
            {
                Assert.That(PacketCodec.TryDecryptMessage(in packet, _session.ReadKey, out Discv5Message message), Is.True);
                using (message) return message.RequestId;
            }
        }

        internal Task Send(Discv5Message message, CancellationToken token)
        {
            byte[] nonce = new byte[PacketCodec.NonceSize];
            BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), Interlocked.Increment(ref _nonce));
            return DeliverPacket(_recipient, _remote.Endpoint, _remote.PacketCodec.EncodeOrdinary(TestItem.PublicKeyB, _session.WriteKey, message, nonce), token);
        }

        public void Dispose()
        {
            _recipient.Outbound.OnSend -= Capture;
            _session.Dispose();
        }
    }

    private static (int Probes, int Refreshes, RequestId RefreshRequestId) CountEndpointResponses(
        TestPeer initiator, TestPeer recipient, Session session)
    {
        int probes = 0;
        int refreshes = 0;
        RequestId requestId = default;
        List<(byte[] Data, IPEndPoint Destination)> deferred = [];
        int pending = recipient.Outbound.Sent.Count;
        for (int index = 0; index < pending && recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) response); index++)
        {
            if (!response.Destination.Equals(initiator.Endpoint))
            {
                deferred.Add(response);
                continue;
            }
            Assert.That(initiator.PacketCodec.TryDecode(response.Data, out Packet packet), Is.True);
            using (packet)
            {
                Assert.That(PacketCodec.TryDecryptMessage(in packet, session.ReadKey, out Discv5Message message), Is.True);
                using (message)
                {
                    if (message is PingMsg) probes++;
                    if (message is FindNodeMsg findNode)
                    {
                        Assert.That(findNode.Distances, Has.Count.EqualTo(1));
                        Assert.That(findNode.Distances[0], Is.Zero);
                        requestId = message.RequestId;
                        refreshes++;
                    }
                }
            }
        }
        foreach ((byte[] Data, IPEndPoint Destination) packet in deferred) recipient.Outbound.Sent.Enqueue(packet);
        return (probes, refreshes, requestId);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Matched_handshakes_have_a_separate_work_budget(
        [Values(511, 512)] int challenges, [Values(0, 32)] int sources, CancellationToken token)
    {
        ManualTimeProvider time = new();
        DiscoveryV5Transport transport = CreateTransport(time);
        await using TestPeer initiator = CreatePeer(TestItem.PrivateKeyA, IPEndPoint.Parse("127.0.0.1:10000"));
        await using TestPeer recipient = CreatePeer(TestItem.PrivateKeyB, IPEndPoint.Parse("127.0.0.1:10001"), inboundTransport: transport);
        using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        byte[] challenge = await RequestChallenge(initiator, recipient, request, 1);
        byte[] handshake = EncodeHandshake(initiator, request, challenge, out Session session);
        using (session)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            Task draining = Drain(recipient, token);
            QueueChallengeRequests(initiator, transport, challenges, sources);
            transport.Receive(PooledUdpReceiveResult.Copy(handshake, initiator.Endpoint));
            transport.Close();
            await draining;
            for (int index = 0; index < challenges; index++)
            {
                Packet packet;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(recipient.Outbound.Sent.TryDequeue(out (byte[] Data, IPEndPoint Destination) sent), Is.True);
                    Assert.That(initiator.PacketCodec.TryDecode(sent.Data, out packet), Is.True);
                }
                using (packet) Assert.That(packet.Flag, Is.EqualTo(PacketFlag.WhoAreYou));
            }
            AssertTalkResponse(initiator, recipient, session, request.RequestId);
            Assert.That(recipient.Outbound.Sent, Is.Empty);
        }
    }

    private static void QueueChallengeRequests(TestPeer initiator, DiscoveryV5Transport transport, int requests, int sources = 0)
    {
        using TalkReqMsg request = new(new RequestId(1, 1), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        for (int index = 0; index < requests; index++)
        {
            byte[] nonce = new byte[PacketCodec.NonceSize];
            nonce[0] = (byte)index;
            nonce[1] = (byte)(index >> 8);
            byte[] ordinary = initiator.PacketCodec.EncodeOrdinary(TestItem.PublicKeyB, new byte[Session.KeySize], request, nonce);
            int sourceIndex = sources == 0 ? index : index % sources;
            IPEndPoint source = new(new IPAddress(new byte[] { 8, 9, (byte)(sourceIndex >> 8), (byte)sourceIndex }), 30303);
            transport.Receive(PooledUdpReceiveResult.Copy(ordinary, source));
        }
    }

    private static DiscoveryV5Transport CreateTransport(ManualTimeProvider time) => new(LimboLogs.Instance)
    {
        ChallengeLimiter = new(512, TimeSpan.FromMilliseconds(100), time),
        SourceLimiter = new(32, 8192, TimeSpan.FromMilliseconds(100), time)
    };

    private static async Task Drain(TestPeer peer, CancellationToken token)
    {
        await foreach (PooledUdpReceiveResult packet in peer.Transport.ReadMessagesAsync(token))
        {
            using (packet) await peer.Adapter.HandlePacket(packet, token);
        }
    }
}
