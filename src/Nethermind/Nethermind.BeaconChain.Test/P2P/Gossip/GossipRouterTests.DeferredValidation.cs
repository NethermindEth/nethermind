// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Threading;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public partial class GossipRouterTests
{
    [TestCase(0UL, false)]
    [TestCase(4096UL, true)]
    public void The_pending_byte_backstop_starts_the_host_for_any_blob_schedule(ulong maxBlobsPerBlock, bool saturated)
    {
        BeaconChainSpec spec = Spec with { BlobSchedule = [.. Spec.BlobSchedule, new BlobScheduleEntry(ulong.MaxValue, maxBlobsPerBlock)] };

        int backstop = BeaconP2P.PendingValidationBytesBackstop(new BeaconChainConfig(), spec);

        Assert.That(backstop, saturated ? Is.EqualTo(int.MaxValue) : Is.LessThan(int.MaxValue), "a backstop past int range must saturate, not fail the host's startup");
    }

    // phase0 p2p-interface.md: REJECT and IGNORE are not forwarded.
    [Test]
    public async Task Deferred_block_is_sent_to_the_mesh_only_once_its_verdict_is_accepted(
        [Values(MessageValidity.Accepted, MessageValidity.Rejected, MessageValidity.Ignored)] MessageValidity validity)
    {
        List<PeerId> rejected = [];
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 8, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), reportRejected: (peer, _) => rejected.Add(peer));
        byte[] block = BlockMessage(CurrentSlot);

        fixture.Receive(fixture.Sender, block);
        bool forwardedWhilePending = fixture.SentToNeighbor(block);
        int pending = fixture.Pubsub.PendingValidationCount;
        bool applied = fixture.Raised.Single().Complete(validity);
        fixture.Receive(fixture.Neighbor, block);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((forwardedWhilePending, pending), Is.EqualTo((false, 1)), "the router holds a deferred message");
        Assert.That(applied, Is.True);
        Assert.That(rejected, Is.EqualTo(validity == MessageValidity.Rejected ? new[] { fixture.Sender } : Array.Empty<PeerId>()));
        Assert.That(fixture.SentToNeighbor(block), Is.EqualTo(validity == MessageValidity.Accepted), "only an accepted message is sent on");
        Assert.That(fixture.Router.GetDropCount(GossipDropReason.Duplicate), Is.Zero, "the router caches the id of a message with a verdict, so a copy is not validated again");
        Assert.That((fixture.Pubsub.PendingValidationCount, fixture.Validation.PendingBytes), Is.EqualTo((0, 0L)), "a given verdict releases its message");
    }

    // phase0 p2p-interface.md: queue bounds SHOULD prevent DoS.
    [Test]
    public async Task Message_past_the_pending_bound_is_throttled_and_validated_once_the_bound_frees([Values] bool byBytes)
    {
        byte[] first = BlockMessage(CurrentSlot);
        byte[] second = BlockMessage(CurrentSlot - 1);
        int firstSize = new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(first) }.CalculateSize();
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: byBytes ? 64 : 1, maxPendingBytes: byBytes ? firstSize + 1 : 1 << 20, TimeSpan.FromSeconds(30));
        ulong throttledBefore = Metrics.BeaconChainGossipThrottled;

        fixture.Receive(fixture.Sender, first);
        fixture.Receive(fixture.Sender, second);
        int raisedWhileFull = fixture.Raised.Count;
        fixture.Raised[0].Complete(MessageValidity.Ignored);
        fixture.Receive(fixture.Sender, second);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(raisedWhileFull, Is.EqualTo(1), "the message past the bound is not validated");
        Assert.That(Metrics.BeaconChainGossipThrottled - throttledBefore, Is.EqualTo(1UL), "the throttled message is counted");
        Assert.That(fixture.Raised, Has.Count.EqualTo(2), "the throttled message is validated once the bound frees");
    }

    [Test]
    public async Task One_peer_holds_at_most_its_share_of_the_pending_messages()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        int share = fixture.Validation.MaxPendingPerSource;

        // One RPC: the router checks all its messages before it dispatches any of them.
        Rpc rpc = new();
        for (int i = 0; i <= share; i++)
        {
            rpc.Publish.Add(new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot - (ulong)i)) });
        }

        fixture.ReceiveRpc(fixture.Sender, rpc);

        int raisedFromSender = fixture.Raised.Count;
        fixture.Receive(fixture.Neighbor, BlockMessage(CurrentSlot - (ulong)share - 1));
        Message vote = new() { Topic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof), Data = ByteString.CopyFrom([1]) };

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((share, raisedFromSender), Is.EqualTo((2, 2)), "the message past the peer's share is throttled");
        Assert.That(fixture.Raised, Has.Count.EqualTo(3), "another peer's message is still validated");
        Assert.That(fixture.Validation.Verify(fixture.Sender, vote), Is.EqualTo(MessageValidity.Deferred), "votes have a separate per-peer share");
    }

    [Test]
    public async Task One_rpc_of_votes_or_columns_from_one_peer_is_held_to_its_share([Values] bool votes)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 128, maxPendingBytes: 1 << 24, TimeSpan.FromSeconds(30), maxPendingVotes: 128);
        byte[] digest = ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot));
        string Topic(int index) => GossipTopics.Topic(digest, votes ? GossipTopics.BeaconAggregateAndProof : GossipTopics.DataColumnSidecarTopicName((ulong)index));
        MessageValidity[] messages = [.. Enumerable.Range(0, 128).Select(subnet => fixture.Validation.Verify(fixture.Sender,
            new Message { Topic = Topic(subnet), Data = ByteString.CopyFrom([(byte)subnet]) }))];

        MessageValidity block = fixture.Validation.Verify(fixture.Sender, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture.Validation.MaxPendingPerSource, Is.LessThan(messages.Length), "fixture: more messages than the peer's share");
        Assert.That(messages.Take(fixture.Validation.MaxPendingPerSource), Is.All.EqualTo(MessageValidity.Deferred));
        Assert.That(messages.Skip(fixture.Validation.MaxPendingPerSource), Is.All.EqualTo(MessageValidity.Throttled));
        Assert.That(fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = Topic(0), Data = ByteString.CopyFrom([0]) }), Is.EqualTo(MessageValidity.Deferred));
        Assert.That(block, Is.EqualTo(MessageValidity.Deferred));
        Assert.That(fixture.Validation.Pending, Is.EqualTo(1), "votes and columns do not consume block reservations");
    }

    // gloas/p2p-interface.md data_column_sidecar: a sidecar whose block is unseen MAY be queued; the queue must not starve other peers' columns.
    [Test]
    public async Task Columns_parked_by_one_peer_leave_room_for_another_peers_column()
    {
        const int share = ColumnGossipRouter.PendingGloasVerdictsPerSource;
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: share, maxPendingBytes: 1 << 24, TimeSpan.FromSeconds(30), gloasColumns: true);
        byte[] digest = ForkDigest.Compute(Sepolia, Sepolia.GetEpoch(GloasColumnSlot));
        Message Column(ulong index, Hash256 root) => new()
        {
            Topic = GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(index)),
            Data = ByteString.CopyFrom(Snappy.CompressToArray(DataColumnSidecarGloas.Encode(DataColumnSidecarGloasTestFixture.BuildSidecar(index, GloasColumnSlot, root)))),
        };

        List<(MessageValidity Verified, Task Validation)> parked = [];
        for (ulong index = 0; index <= share; index++)
        {
            Message column = Column(index, Keccak.Compute($"unseen block {index}"));
            MessageValidity verified = fixture.Validation.Verify(fixture.Sender, column);
            parked.Add((verified, verified == MessageValidity.Deferred ? fixture.Validation.ValidateAsync(fixture.Sender, column) : Task.CompletedTask));
        }

        MessageValidity neighbor = fixture.Validation.Verify(fixture.Neighbor, Column(share + 1, Keccak.Compute("another unseen block")));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(parked.Select(static entry => entry.Verified), Is.All.EqualTo(MessageValidity.Deferred), "a parked column gives back its reservation");
        Assert.That(parked.Select(static entry => entry.Validation.IsCompleted), Is.EqualTo(Enumerable.Repeat(false, share).Append(true)), "the peer's column past its share is not parked");
        Assert.That(neighbor, Is.EqualTo(MessageValidity.Deferred), "another peer's column is still validated");
        Assert.That(fixture.Validation.PendingColumns, Is.EqualTo(1));
    }

    [Test]
    public async Task Votes_are_held_to_their_own_bound_and_leave_the_room_of_other_gossip()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), maxPendingVotes: 2);
        string aggregates = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof);
        MessageValidity[] votes = [.. Enumerable.Range(0, 3).Select(i => fixture.Validation.Verify(i == 1 ? fixture.Neighbor : fixture.Sender, new Message { Topic = aggregates, Data = ByteString.CopyFrom([(byte)i]) }))];

        MessageValidity block = fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(votes, Is.EqualTo(new[] { MessageValidity.Deferred, MessageValidity.Deferred, MessageValidity.Throttled }));
        Assert.That(block, Is.EqualTo(MessageValidity.Deferred));
        Assert.That((fixture.Validation.PendingVotes, fixture.Validation.Pending), Is.EqualTo((2, 1)), "each deferred message is reserved before it is dispatched");
    }

    [Test]
    public async Task Verdict_not_given_in_time_is_abandoned_and_a_later_copy_is_validated_again()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 8, maxPendingBytes: 1 << 20, TimeSpan.FromMilliseconds(200));
        byte[] block = BlockMessage(CurrentSlot);
        ulong abandonedBefore = Metrics.BeaconChainGossipVerdictsAbandoned;
        ulong lateBefore = Metrics.BeaconChainGossipVerdictsLate;

        fixture.Receive(fixture.Sender, block);
        GossipVerdict abandoned = fixture.Raised.Single();
        await abandoned.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        bool lateApplied = abandoned.Complete(MessageValidity.Accepted);
        for (int i = 0; i < 500 && fixture.Pubsub.PendingValidationCount > 0; i++)
        {
            await Task.Delay(10);
        }

        int pendingAfter = fixture.Pubsub.PendingValidationCount;
        fixture.Receive(fixture.Neighbor, block);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(lateApplied, Is.False);
        Assert.That(fixture.SentToNeighbor(block), Is.False, "a late verdict forwards nothing");
        Assert.That((pendingAfter, fixture.Validation.PendingBytes), Is.EqualTo((0, 0L)));
        Assert.That(Metrics.BeaconChainGossipVerdictsAbandoned - abandonedBefore, Is.EqualTo(1UL));
        Assert.That(Metrics.BeaconChainGossipVerdictsLate - lateBefore, Is.EqualTo(1UL));
        Assert.That(fixture.Router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(1), "the router validated the copy again, as it cached no id for the abandoned message");
    }

    // phase0 p2p-interface.md beacon_block: future slots are IGNOREd and MAY be queued.
    [Test]
    public void Early_next_slot_block_is_ignored_at_once_and_imported_once_its_slot_starts()
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 10));
        GossipRouter router = new(Spec, new SlotClock(Spec, timestamper), LimboLogs.Instance);
        List<GossipVerdict> raised = [];
        router.BeaconBlockReceived += (_, verdict) => raised.Add(verdict);
        GossipVerdict verdict = new(static _ => true, null);

        MessageValidity validity = router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, BlockMessage(CurrentSlot + 1), verdict);
        (bool handedOff, int raisedEarly) = (verdict.IsHandedOff, raised.Count);
        timestamper.UtcNow = timestamper.UtcNow.AddSeconds(Spec.SecondsPerSlot);
        router.ReleaseDueMessages();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((validity, handedOff, raisedEarly), Is.EqualTo((MessageValidity.Ignored, false, 0)), "the caller gives the early block's IGNORE at once");
        Assert.That(raised, Has.Count.EqualTo(1), "the held block is raised once its slot starts");
        Assert.That(raised.SingleOrDefault(), Is.Not.SameAs(verdict), "with a verdict no router waits on");
    }

    [Test]
    public void Message_refused_for_local_load_is_checked_again_when_a_copy_arrives([Values] bool heldForItsSlot)
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 10));
        GossipRouter router = new(Spec, new SlotClock(Spec, timestamper), LimboLogs.Instance);
        int raised = 0;
        router.BeaconBlockReceived += (_, verdict) =>
        {
            if (++raised == 1)
            {
                verdict.Complete(MessageValidity.Throttled);
            }
        };
        ulong slot = heldForItsSlot ? CurrentSlot + 1 : CurrentSlot;

        router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, BlockMessage(slot), new GossipVerdict(static _ => true, null));
        timestamper.UtcNow = timestamper.UtcNow.AddSeconds(Spec.SecondsPerSlot);
        router.ReleaseDueMessages();
        router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, BlockMessage(slot), new GossipVerdict(static _ => true, null));

        Assert.That((raised, router.GetDropCount(GossipDropReason.Duplicate)), Is.EqualTo((2, 0L)));
    }

    [Test]
    public async Task Reservation_of_a_message_never_dispatched_is_released_when_it_expires()
    {
        ManualTimeProvider time = new();
        time.JumpUtc(TimeSpan.FromDays(20_000));
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), time: time);
        string aggregates = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof);
        fixture.Validation.Verify(fixture.Sender, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });
        fixture.Validation.Verify(fixture.Sender, new Message { Topic = aggregates, Data = ByteString.CopyFrom([1]) });
        (int pending, int votes) = (fixture.Validation.Pending, fixture.Validation.PendingVotes);

        time.Advance(TimeSpan.FromSeconds(29));
        fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot - 1)) });
        (int pendingBeforeExpiry, int votesBeforeExpiry) = (fixture.Validation.Pending, fixture.Validation.PendingVotes);
        time.Advance(TimeSpan.FromSeconds(3));
        fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot - 2)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((pending, votes), Is.EqualTo((1, 1)), "fixture: both messages reserved and never dispatched");
        Assert.That((pendingBeforeExpiry, votesBeforeExpiry), Is.EqualTo((2, 1)), "a reservation lasts as long as the router may still dispatch its message");
        Assert.That((fixture.Validation.Pending, fixture.Validation.PendingVotes), Is.EqualTo((2, 0)), "the expired reservations are released, the later ones kept");
    }

    internal static async Task AssertDeferredPeerPenaltyAsync(string name, byte[] payload, Func<GossipVerdict, Task> validate, MessageValidity expected)
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        string topic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), name);
        using PubsubRouter pubsub = new(new PeerStore(), GossipScoring.Configure(p2p.PubsubSettingsForTest, [topic], Spec));
        pubsub.GetTopic(topic);
        (PeerId sender, _, Action<Rpc> receive) = ConnectSubscribedPeer(pubsub, topic);
        (PeerId neighbor, List<Rpc> forwarded, _) = ConnectSubscribedPeer(pubsub, topic);
        IRoutingStateContainer routing = pubsub;
        routing.Mesh[topic].UnionWith([sender, neighbor]);
        TaskCompletionSource<GossipVerdict> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MessageValidity? given = null;
        pubsub.VerifyMessage = static (_, _) => MessageValidity.Deferred;
        pubsub.OnDeferredMessage = (_, message) =>
        {
            GossipVerdict verdict = new(validity =>
            {
                given = validity;
                return pubsub.CompleteValidation(message, validity);
            }, null);
            pending.SetResult(verdict);
            return verdict.Completion;
        };
        Rpc rpc = new();
        rpc.Publish.Add(new Message { Topic = topic, Data = ByteString.CopyFrom(payload) });
        receive(rpc);
        GossipVerdict deferred = await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(forwarded.SelectMany(static rpc => rpc.Publish), Is.Empty, "pending messages are not forwarded");
        await validate(deferred);
        await pubsub.Heartbeat();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(given, Is.EqualTo(expected), "the worker must distinguish invalid input from unavailable local data");
        Assert.That(forwarded.SelectMany(static rpc => rpc.Publish).Select(static message => message.Data.ToByteArray()),
            Is.EqualTo(expected == MessageValidity.Accepted ? new[] { payload } : []));
        Assert.That(routing.Mesh[topic].Contains(sender), Is.EqualTo(expected != MessageValidity.Rejected), "invalid-message scoring charges the delivering peer");
        Assert.That(routing.Mesh[topic], Does.Contain(neighbor), "a peer that did not deliver the invalid message keeps its score");
    }

    private static readonly ulong GloasColumnSlot = FirstGloasSlot + 1;
    private static readonly string BlockTopic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconBlock);

    private sealed class DeferredFixture(IContainer container, PubsubRouter pubsub, GossipRouter router, DeferredGossipValidation validation, List<GossipVerdict> raised,
        (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) sender, (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) neighbor) : IAsyncDisposable
    {
        public PubsubRouter Pubsub { get; } = pubsub;
        public GossipRouter Router { get; } = router;
        public DeferredGossipValidation Validation { get; } = validation;
        public List<GossipVerdict> Raised { get; } = raised;
        public PeerId Sender { get; } = sender.Peer;
        public PeerId Neighbor { get; } = neighbor.Peer;

        public static async Task<DeferredFixture> Create(int maxPending, long maxPendingBytes, TimeSpan timeout, string protocol = PubsubRouter.GossipsubProtocolVersionV11, int maxPendingVotes = 1024,
            TimeProvider? time = null, bool gloasColumns = false, Action<PeerId, string>? reportRejected = null)
        {
            IContainer container = BeaconChainTestContainer.Builder().Build();
            PubsubSettings settings;
            await using (BeaconP2P p2p = container.Resolve<BeaconP2P>())
            {
                settings = p2p.PubsubSettingsForTest;
            }

            PubsubRouter pubsub = new(new PeerStore(), settings);
            SlotClock clock = new(Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 6)));
            GossipRouter router = new(Spec, clock, LimboLogs.Instance);
            List<GossipVerdict> raised = [];
            router.BeaconBlockReceived += (_, verdict) => raised.Add(verdict);
            GossipMessageValidator validator = new(router, new ColumnGossipRouter(Spec, clock, LimboLogs.Instance), Spec, clock);
            if (gloasColumns)
            {
                // No store holds a block, so every Gloas sidecar for the current slot is parked.
                SlotClock gloasClock = new(Sepolia, new ManualTimestamper(SepoliaSlotStart(GloasColumnSlot).AddSeconds(6)));
                ColumnGossipRouter columns = new(Sepolia, gloasClock, LimboLogs.Instance);
                columns.Start(_ => new GossipDigestWindowTests.SilentTopic(), ForkDigest.Compute(Sepolia, Sepolia.GetEpoch(GloasColumnSlot)),
                    [.. Enumerable.Range(0, (int)Eip7594DasConstants.DataColumnSidecarSubnetCount).Select(static subnet => (ulong)subnet)]);
                validator = new(new GossipRouter(Sepolia, gloasClock, LimboLogs.Instance), columns, Sepolia, gloasClock);
            }

            DeferredGossipValidation validation = new(pubsub, validator,
                maxPending, maxPendingBytes, maxPendingVotes, timeout, LimboLogs.Instance.GetClassLogger<GossipRouterTests>(), CancellationToken.None, time)
            {
                ReportRejectedMessage = reportRejected,
            };
            pubsub.VerifyMessage = validation.Verify;
            pubsub.OnDeferredMessage = validation.ValidateAsync;
            pubsub.GetTopic(BlockTopic);
            (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) sender = ConnectSubscribedPeer(pubsub, BlockTopic, protocol);
            (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) neighbor = ConnectSubscribedPeer(pubsub, BlockTopic, protocol);
            ((IRoutingStateContainer)pubsub).Mesh[BlockTopic].UnionWith([sender.Peer, neighbor.Peer]);
            return new DeferredFixture(container, pubsub, router, validation, raised, sender, neighbor);
        }

        public void Receive(PeerId from, byte[] data)
        {
            Rpc rpc = new();
            rpc.Publish.Add(new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(data) });
            ReceiveRpc(from, rpc);
        }

        public void ReceiveRpc(PeerId from, Rpc rpc) => (from == Sender ? sender.Receive : neighbor.Receive)(rpc);
        public IReadOnlyList<Rpc> SentTo(PeerId peer) => peer == Sender ? sender.Sent : neighbor.Sent;

        public IEnumerable<MessageId> IdontwantsTo(PeerId peer) =>
            SentTo(peer).SelectMany(static rpc => rpc.Control?.Idontwant ?? []).SelectMany(static idontwant => idontwant.MessageIDs).Select(static id => new MessageId(id.ToByteArray()));

        public bool SentToNeighbor(byte[] data) =>
            neighbor.Sent.SelectMany(static rpc => rpc.Publish).Any(message => message.Topic == BlockTopic && message.Data.Span.SequenceEqual(data));

        public ValueTask DisposeAsync()
        {
            Pubsub.Dispose();
            container.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
