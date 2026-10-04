// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class GossipLoopbackTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    /// <summary>A block this host publishes passes the other host's <c>StrictNoSign</c> check and validator and reaches its gossip router.</summary>
    /// <remarks>p2p-interface.md "Topics and messages": a published message omits from, seqno, signature and key, which the receiver enforces.</remarks>
    [Test]
    [CancelAfter(120_000)]
    public async Task Block_published_on_one_host_reaches_the_gossip_router_on_the_other(CancellationToken token)
    {
        SlotClock slotClock = new(Spec, Timestamper.Default);
        byte[] digest = ForkDigest.Compute(Spec, slotClock.CurrentEpoch);
        string blockTopic = GossipTopics.Topic(digest, GossipTopics.BeaconBlock);
        GossipRouter router = new(Spec, slotClock, LimboLogs.Instance);

        await using BeaconP2P publisher = CreateHost();
        await using BeaconP2P subscriber = CreateHost(new GossipMessageValidator(router, new ColumnGossipRouter(Spec, slotClock, LimboLogs.Instance), Spec, slotClock));
        await publisher.StartAsync(token);
        await subscriber.StartAsync(token);

        ITopic publisherTopic = publisher.GetTopic(blockTopic);
        router.Start(subscriber.GetTopic, digest);
        TaskCompletionSource<ForkedSignedBeaconBlock> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeaconBlockReceived += (block, _) => received.TrySetResult(block);

        await ConnectAsync(subscriber, publisher, token);

        // Republish (with distinct payloads, so dedup cannot hide a delivery) until the mesh has
        // formed and a message makes it across.
        for (ulong attempt = 0; !received.Task.IsCompleted; attempt++)
        {
            SignedBeaconBlock block = TestChain.CreateBlock(slotClock.CurrentSlot, new Hash256(ValueKeccak.Compute([(byte)attempt]).Bytes));
            publisherTopic.Publish(Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));
            await Task.WhenAny(received.Task, Task.Delay(500, token));
            token.ThrowIfCancellationRequested();
        }

        ForkedSignedBeaconBlock receivedBlock = await received.Task;
        Assert.That(receivedBlock.Slot, Is.EqualTo(slotClock.CurrentSlot).Within(1), "the published block round-trips the mesh");
    }

    /// <summary>A column this host reconstructs reaches a mesh neighbor on that column's subnet, which enforces <c>StrictNoSign</c>.</summary>
    /// <remarks>fulu/das-core.md "Reconstruction and cross-seeding": a reconstructed column of a subscribed subnet MUST be sent to the topic mesh neighbors.</remarks>
    [Test]
    [CancelAfter(120_000)]
    public async Task Reconstructed_column_reaches_the_mesh_neighbor_on_its_subnet(CancellationToken token)
    {
        const ulong slot = 13_410_304;
        const ulong reconstructed = Eip7594DasConstants.RequiredColumnsForReconstruction;
        SlotClock slotClock = new(Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + slot * Spec.SecondsPerSlot + 6)));
        byte[] digest = ForkDigest.Compute(Spec, Spec.GetEpoch(slot));
        string topicId = GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(reconstructed));
        // An imported block's header, so its sidecars pass every check and its reconstructed columns may be published.
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        DataColumnSidecarTestFixture.StoreAsImported(store, DataColumnSidecarTestFixture.BuildValidSidecar(0, slot));
        ColumnGossipRouter columns = new(Spec, slotClock, LimboLogs.Instance, new DataColumnSidecarPool(), store);

        await using BeaconP2P reconstructing = CreateHost(new GossipMessageValidator(new GossipRouter(Spec, slotClock, LimboLogs.Instance), columns, Spec, slotClock));
        await using BeaconP2P neighbor = CreateHost();
        await reconstructing.StartAsync(token);
        await neighbor.StartAsync(token);
        columns.Start(reconstructing.GetTopic, digest, [.. Enumerable.Range(0, (int)reconstructed + 1).Select(static subnet => (ulong)subnet)]);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        neighbor.GetTopic(topicId).OnMessage += (_, data) => received.TrySetResult(data);
        await ConnectAsync(neighbor, reconstructing, token);
        while (!IsMeshNeighbor(reconstructing, topicId, neighbor.LocalPeerId!))
        {
            await Task.Delay(100, token);
        }

        for (ulong column = 0; column < reconstructed; column++)
        {
            columns.Handle(column, gloasTopic: false, Snappy.CompressToArray(DataColumnSidecar.Encode(DataColumnSidecarTestFixture.BuildValidSidecar(column, slot))));
        }

        Assert.That(await received.Task, Is.EqualTo(Snappy.CompressToArray(DataColumnSidecar.Encode(DataColumnSidecarTestFixture.BuildValidSidecar(reconstructed, slot)))));
    }

    private static bool IsMeshNeighbor(BeaconP2P node, string topicId, PeerId peer)
    {
        IRoutingStateContainer router = node.RoutingStateForTest!;
        lock (router)
        {
            return router.Mesh.TryGetValue(topicId, out HashSet<PeerId>? mesh) && mesh.Contains(peer);
        }
    }

    /// <summary>
    /// A message the relay defers reaches its other mesh neighbor only once its verdict is <see cref="MessageValidity.Accepted"/>; a
    /// <see cref="MessageValidity.Rejected"/> one never does, and its delivering peer, not the neighbor, is pruned from the relay's mesh.
    /// </summary>
    /// <remarks>phase0 p2p-interface.md "Topics and messages": ACCEPT once every validation passed; a REJECTed message is not forwarded and MAY descore its sender.</remarks>
    [TestCase(MessageValidity.Accepted)]
    [TestCase(MessageValidity.Rejected)]
    [TestCase(MessageValidity.Ignored)]
    [CancelAfter(120_000)]
    public async Task Relay_forwards_a_deferred_block_only_once_it_is_accepted_and_charges_its_sender_for_a_reject(MessageValidity verdict, CancellationToken token)
    {
        SlotClock slotClock = new(Spec, Timestamper.Default);
        byte[] digest = ForkDigest.Compute(Spec, slotClock.CurrentEpoch);
        string blockTopic = GossipTopics.Topic(digest, GossipTopics.BeaconBlock);
        GossipRouter router = new(Spec, slotClock, LimboLogs.Instance);
        TaskCompletionSource<GossipVerdict> raised = new(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeaconBlockReceived += (_, pending) => raised.TrySetResult(pending);

        await using BeaconP2P sender = CreateHost();
        await using BeaconP2P relay = CreateHost(new GossipMessageValidator(router, new ColumnGossipRouter(Spec, slotClock, LimboLogs.Instance), Spec, slotClock));
        await using BeaconP2P neighbor = CreateHost();
        await sender.StartAsync(token);
        await relay.StartAsync(token);
        await neighbor.StartAsync(token);
        router.Start(relay.GetTopic, digest);
        ITopic senderTopic = sender.GetTopic(blockTopic);
        TaskCompletionSource<byte[]> forwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        neighbor.GetTopic(blockTopic).OnMessage += (_, data) => forwarded.TrySetResult(data);
        await ConnectAsync(sender, relay, token);
        await ConnectAsync(neighbor, relay, token);
        while (!IsMeshNeighbor(relay, blockTopic, sender.LocalPeerId!) || !IsMeshNeighbor(relay, blockTopic, neighbor.LocalPeerId!)
            || !IsMeshNeighbor(sender, blockTopic, relay.LocalPeerId!))
        {
            await Task.Delay(100, token);
        }

        byte[] message = Snappy.CompressToArray(SignedBeaconBlock.Encode(TestChain.CreateBlock(slotClock.CurrentSlot, Hash256.Zero)));
        senderTopic.Publish(message);
        GossipVerdict pending = await raised.Task.WaitAsync(token);
        bool forwardedEarly = await Task.WhenAny(forwarded.Task, Task.Delay(2_000, token)) == forwarded.Task;
        int pendingAtRelay = ((PubsubRouter)relay.RoutingStateForTest!).PendingValidationCount;

        bool applied = pending.Complete(verdict);
        bool forwardedAfter = await Task.WhenAny(forwarded.Task, Task.Delay(verdict == MessageValidity.Accepted ? 30_000 : 3_000, token)) == forwarded.Task;
        await ((IRoutingStateContainer)relay.RoutingStateForTest!).Heartbeat();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((forwardedEarly, pendingAtRelay), Is.EqualTo((false, 1)), "the relay holds the message while its verdict is pending");
        Assert.That(applied, Is.True, "the router applied the verdict");
        Assert.That(forwardedAfter, Is.EqualTo(verdict == MessageValidity.Accepted), "only an accepted message is forwarded");
        if (forwardedAfter)
        {
            Assert.That(await forwarded.Task, Is.EqualTo(message));
        }

        Assert.That(IsMeshNeighbor(relay, blockTopic, sender.LocalPeerId!), Is.EqualTo(verdict != MessageValidity.Rejected), "only a rejected message's sender is pruned");
        Assert.That(IsMeshNeighbor(relay, blockTopic, neighbor.LocalPeerId!), Is.True, "the neighbor that sent nothing keeps its place");
    }

    /// <summary>A data column sidecar that passes every check on the relay, under an imported block's header, reaches the relay's other mesh neighbor.</summary>
    /// <remarks>fulu/p2p-interface.md data_column_sidecar_{subnet_id}: a sidecar that passes every check is accepted, so the router forwards it.</remarks>
    [Test]
    [CancelAfter(120_000)]
    public async Task Relay_forwards_a_column_that_passes_every_check(CancellationToken token)
    {
        const ulong slot = 13_410_304;
        const ulong column = 5;
        SlotClock slotClock = new(Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + slot * Spec.SecondsPerSlot + 6)));
        byte[] digest = ForkDigest.Compute(Spec, Spec.GetEpoch(slot));
        string topicId = GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(column));
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, slot);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        DataColumnSidecarTestFixture.StoreAsImported(store, sidecar);
        ColumnGossipRouter columns = new(Spec, slotClock, LimboLogs.Instance, new DataColumnSidecarPool(), store);

        await using BeaconP2P sender = CreateHost();
        await using BeaconP2P relay = CreateHost(new GossipMessageValidator(new GossipRouter(Spec, slotClock, LimboLogs.Instance), columns, Spec, slotClock));
        await using BeaconP2P neighbor = CreateHost();
        await sender.StartAsync(token);
        await relay.StartAsync(token);
        await neighbor.StartAsync(token);
        columns.Start(relay.GetTopic, digest, [column]);
        ITopic senderTopic = sender.GetTopic(topicId);
        TaskCompletionSource<byte[]> forwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        neighbor.GetTopic(topicId).OnMessage += (_, data) => forwarded.TrySetResult(data);
        await ConnectAsync(sender, relay, token);
        await ConnectAsync(neighbor, relay, token);
        while (!IsMeshNeighbor(relay, topicId, neighbor.LocalPeerId!) || !IsMeshNeighbor(sender, topicId, relay.LocalPeerId!))
        {
            await Task.Delay(100, token);
        }

        byte[] message = Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar));
        senderTopic.Publish(message);

        Assert.That(await forwarded.Task, Is.EqualTo(message));
    }

    /// <summary>
    /// A digest rotation leaves the retired topics on the wire: a connected peer learns the node left them, and a peer that connects
    /// afterwards is told only of the topics the node still subscribes.
    /// </summary>
    /// <remarks>altair/p2p-interface.md "Transitioning the gossip": two epochs after the fork, pre-fork topics SHOULD be unsubscribed from.</remarks>
    [Test]
    [CancelAfter(120_000)]
    public async Task Retired_topics_are_left_on_the_wire_and_not_announced_to_a_later_peer(CancellationToken token)
    {
        SlotClock slotClock = new(Spec, Timestamper.Default);
        byte[] retiredDigest = ForkDigest.Compute(Spec, slotClock.CurrentEpoch);
        byte[] currentDigest = ForkDigest.Compute(Spec, 0);
        string retired = GossipTopics.Topic(retiredDigest, GossipTopics.BeaconBlock);
        string current = GossipTopics.Topic(currentDigest, GossipTopics.BeaconBlock);
        GossipRouter router = new(Spec, slotClock, LimboLogs.Instance);

        await using BeaconP2P node = CreateHost(new GossipMessageValidator(router, new ColumnGossipRouter(Spec, slotClock, LimboLogs.Instance), Spec, slotClock));
        await using BeaconP2P connected = CreateHost();
        await using BeaconP2P later = CreateHost();
        await node.StartAsync(token);
        await connected.StartAsync(token);
        await later.StartAsync(token);
        router.Start(node.GetTopic, retiredDigest);
        router.SubscribeDigest(currentDigest);
        await ConnectAsync(connected, node, token);
        while (!Subscribes(connected, retired, node.LocalPeerId!))
        {
            await Task.Delay(100, token);
        }

        router.UnsubscribeDigest(retiredDigest);
        while (Subscribes(connected, retired, node.LocalPeerId!))
        {
            await Task.Delay(100, token);
        }

        await ConnectAsync(later, node, token);
        while (!Subscribes(later, current, node.LocalPeerId!))
        {
            await Task.Delay(100, token);
        }

        Assert.That(Subscribes(later, retired, node.LocalPeerId!), Is.False, "a peer connecting after the rotation is not told of the retired topic");
    }

    /// <summary>Whether <paramref name="observer"/> knows <paramref name="peer"/> as a gossipsub subscriber of <paramref name="topicId"/>.</summary>
    private static bool Subscribes(BeaconP2P observer, string topicId, PeerId peer)
    {
        IRoutingStateContainer router = observer.RoutingStateForTest!;
        lock (router)
        {
            return router.GossipsubPeers.TryGetValue(topicId, out HashSet<PeerId>? peers) && peers.Contains(peer);
        }
    }

    /// <summary>
    /// Two nodes whose session the peer manager admits exchange gossip with no pubsub discovery, whichever side dialed: the router opens its
    /// own channel only when told of a peer, which the identify probe this node runs never does, or in answer to the remote's channel.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task Nodes_admitted_by_the_peer_manager_exchange_gossip_without_discovery([Values] bool admittedInbound, CancellationToken token)
    {
        string topicId = GossipTopics.Topic(ForkDigest.Compute(Spec, 0), GossipTopics.BeaconBlock);
        await using BeaconP2P publisher = CreateHost();
        await using BeaconP2P subscriber = CreateHost();
        await publisher.StartAsync(token);
        await subscriber.StartAsync(token);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.GetTopic(topicId).OnMessage += (_, data) => received.TrySetResult(data);
        ITopic topic = publisher.GetTopic(topicId);
        if (admittedInbound)
        {
            // Only the subscriber runs a peer manager, which admits the session the publisher opens.
            PeerManager manager = CreatePeerManager(subscriber);
            await publisher.DialPeerAsync(PeerSessionNodes.LoopbackAddress(subscriber), token);
            await PeerSessionNodes.WaitUntilAsync(() => manager.PeerCount == 1, "the subscriber never admitted the session", token);
        }
        else
        {
            await ConnectAsync(publisher, subscriber, token);
        }

        byte[] message = [1, 2, 3];
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        while (!received.Task.IsCompleted && !bounded.IsCancellationRequested)
        {
            // The publisher sends only once the subscriber is in its mesh, so it repeats until the message arrives.
            topic.Publish(message);
            await Task.WhenAny(received.Task, Task.Delay(500, CancellationToken.None));
        }

        Assert.That(received.Task.IsCompleted, Is.True, "no gossip crossed the admitted session");
        Assert.That(await received.Task, Is.EqualTo(message));
    }

    /// <summary>
    /// A gossip channel that ends while its session lives, here closed by the remote, is opened again by the peer manager, so gossip resumes
    /// on the same session; before, the peer stayed admitted with no gossip for the rest of the session.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task Gossip_resumes_when_the_channel_ends_while_the_session_lives(CancellationToken token)
    {
        string topicId = GossipTopics.Topic(ForkDigest.Compute(Spec, 0), GossipTopics.BeaconBlock);
        await using BeaconP2P node = CreateHost();
        await using BeaconP2P remote = CreateHost();
        await node.StartAsync(token);
        await remote.StartAsync(token);
        Dictionary<byte, TaskCompletionSource> received = new() { [1] = new(TaskCreationOptions.RunContinuationsAsynchronously), [2] = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        node.GetTopic(topicId).OnMessage += (_, data) => { if (data is [byte id] && received.TryGetValue(id, out TaskCompletionSource? arrived)) arrived.TrySetResult(); };
        ITopic topic = remote.GetTopic(topicId);

        // The remote opens the first channel itself, under a token the test holds, and the node's router answers with its own.
        ISession session = await remote.DialPeerAsync(PeerSessionNodes.LoopbackAddress(node), token);
        using CancellationTokenSource remoteChannel = CancellationTokenSource.CreateLinkedTokenSource(token);
        _ = session.DialAsync<GossipsubProtocolV13>(remoteChannel.Token);
        await PeerSessionNodes.WaitUntilAsync(() => node.HasGossipChannel(remote.LocalPeerId!) && remote.HasGossipChannel(node.LocalPeerId!), "the remote's channel was never answered", token);
        PeerManager manager = CreatePeerManager(node);
        manager.GossipChannelCheckInterval = TimeSpan.FromSeconds(5);
        Assert.That(await manager.TryAddPeerAsync(PeerSessionNodes.LoopbackAddressText(remote), token), Is.True, "the peer manager did not admit the session");
        Assert.That(await DeliversAsync(topic, 1, received[1].Task, token), Is.True, "no gossip crossed the admitted session");

        remoteChannel.Cancel();
        await PeerSessionNodes.WaitUntilAsync(() => !node.HasGossipChannel(remote.LocalPeerId!), "closing the remote's channel did not end the node's", token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(await DeliversAsync(topic, 2, received[2].Task, token), Is.True, "gossip did not resume after the channel ended");
        Assert.That(node.SessionCountForTest, Is.EqualTo(1), "the same session carries the new channel");
    }

    /// <summary>
    /// A session the remote opens while the pool still holds the record of its dropped session is admitted and gets a gossip channel: the
    /// libp2p layer holds one session per peer id, so that record is one whose close callback has not run yet, not a live duplicate.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task A_session_that_replaces_a_dropped_one_still_in_the_pool_is_admitted_with_gossip(CancellationToken token)
    {
        await using BeaconP2P node = CreateHost();
        await using BeaconP2P remote = CreateHost();
        await node.StartAsync(token);
        await remote.StartAsync(token);
        ISession dropped = await node.DialPeerAsync(PeerSessionNodes.LoopbackAddress(remote), token);
        ISession? accepted = null;
        await PeerSessionNodes.WaitUntilAsync(() => remote.TryGetEstablishedSession(node.LocalPeerId!, out accepted), "fixture: the remote never recorded the session", token);
        // The libp2p listener can keep an idle session after the dialer closes it, so both ends close it.
        await dropped.DisconnectAsync();
        await accepted!.DisconnectAsync();
        Assert.That((node.SessionCountForTest, remote.SessionCountForTest), Is.EqualTo((0, 0)), "fixture: the dropped session is closed at both ends");
        PeerManager manager = CreatePeerManager(node);
        manager.AddPeerForTest(dropped, PeerSessionNodes.LoopbackAddressText(remote), removeWhenSessionCloses: false);
        TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.PeerAdmitted += _ => admitted.TrySetResult();

        await remote.DialPeerAsync(PeerSessionNodes.LoopbackAddress(node), token);

        await PeerSessionNodes.WaitUntilAsync(() => admitted.Task.IsCompleted, "the new session was taken for a duplicate of the dropped one", token, TimeSpan.FromSeconds(30));
        await PeerSessionNodes.WaitUntilAsync(() => node.HasGossipChannel(remote.LocalPeerId!) && remote.HasGossipChannel(node.LocalPeerId!), "the new session got no gossip channel", token);
    }

    /// <summary>Each gossip channel that does not last doubles the wait before the next is opened, up to a minute, so a peer that keeps closing it costs few dials.</summary>
    [TestCase(1, 2)]
    [TestCase(40, 60)]
    [TestCase(60, 60)]
    public void Wait_before_reopening_a_gossip_channel_doubles_up_to_its_bound(int seconds, int expectedSeconds) =>
        Assert.That(PeerManager.NextGossipRedialDelay(TimeSpan.FromSeconds(seconds)), Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));

    /// <summary>Publishes <paramref name="id"/> until <paramref name="received"/> ends or 30 s pass; the publisher sends only to peers whose subscription it has learnt.</summary>
    private static async Task<bool> DeliversAsync(ITopic topic, byte id, Task received, CancellationToken token)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        while (!received.IsCompleted && !bounded.IsCancellationRequested)
        {
            topic.Publish([id]);
            await Task.WhenAny(received, Task.Delay(500, CancellationToken.None));
        }

        return received.IsCompleted;
    }

    /// <summary>A gossip message of any legal size crosses a real session whole, so its sender is not disconnected for a truncated RPC.</summary>
    /// <remarks>p2p-interface.md "Gossipsub size limits" allow a compressed payload up to max_compressed_len(10 MiB); a message over one yamux window
    /// spans several frames, so this exercises segmented channel reads across yamux windows.</remarks>
    [TestCase(64)]
    [TestCase(300 * 1024)]
    [TestCase(1200 * 1024)]
    [TestCase(6 * 1024 * 1024)]
    [CancelAfter(60_000)]
    public async Task A_gossip_message_of_any_legal_size_reaches_the_other_host(int size, CancellationToken token)
    {
        string topicId = GossipTopics.Topic(ForkDigest.Compute(Spec, 0), GossipTopics.DataColumnSidecarTopicName(0));
        await using BeaconP2P publisher = CreateHost();
        await using BeaconP2P subscriber = CreateHost();
        await publisher.StartAsync(token);
        await subscriber.StartAsync(token);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.GetTopic(topicId).OnMessage += (_, data) => received.TrySetResult(data);
        ITopic topic = publisher.GetTopic(topicId);
        await ConnectAsync(subscriber, publisher, token);

        byte[] message = new byte[size];
        Random.Shared.NextBytes(message);
        while (!received.Task.IsCompleted)
        {
            // The publisher sends only once it has learnt the subscription, so it repeats until the subscriber has the message.
            token.ThrowIfCancellationRequested();
            topic.Publish(message);
            await Task.WhenAny(received.Task, Task.Delay(500, token));
        }

        Assert.That(await received.Task, Is.EqualTo(message));
    }

    /// <summary>Connects <paramref name="from"/> to <paramref name="to"/> through its peer manager's admission, as the node does, with no pubsub discovery.</summary>
    private static async Task<PeerManager> ConnectAsync(BeaconP2P from, BeaconP2P to, CancellationToken token)
    {
        PeerManager manager = CreatePeerManager(from);
        Assert.That(await manager.TryAddPeerAsync(PeerSessionNodes.LoopbackAddressText(to), token), Is.True, "the peer manager did not admit the peer");
        return manager;
    }

    private static PeerManager CreatePeerManager(BeaconP2P node) =>
        new(node, new BeaconChainConfig { P2PPort = 0 }, new BeaconChainStatusHolder(Spec, Timestamper.Default) { CurrentStatus = PeerSessionNodes.Status }, LimboLogs.Instance);

    private static BeaconP2P CreateHost(GossipMessageValidator? validator = null)
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        return new BeaconP2P(config, Spec, store, new BeaconChainStatusHolder(Spec, Timestamper.Default) { CurrentStatus = PeerSessionNodes.Status }, new LocalMetadataSource(), new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance,
            validator);
    }
}
