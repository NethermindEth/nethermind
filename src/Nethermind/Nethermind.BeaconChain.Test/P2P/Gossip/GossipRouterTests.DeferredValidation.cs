// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Google.Protobuf;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

/// <summary>The router's deferred validation of eth2 gossip under the host's settings, with simulated peers.</summary>
public partial class GossipRouterTests
{
    /// <summary>
    /// A block the router defers is sent to the other mesh peer only once its verdict is <see cref="MessageValidity.Accepted"/>, and
    /// a verdict other than accept leaves its id cached, so the same message from another peer is not validated again.
    /// </summary>
    /// <remarks>phase0 p2p-interface.md "Topics and messages": ACCEPT once every validation passed; REJECT and IGNORE are not forwarded.</remarks>
    [TestCase(MessageValidity.Accepted)]
    [TestCase(MessageValidity.Rejected)]
    [TestCase(MessageValidity.Ignored)]
    public async Task Deferred_block_is_sent_to_the_mesh_only_once_its_verdict_is_accepted(MessageValidity validity)
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 8, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30));
        byte[] block = BlockMessage(CurrentSlot);

        fixture.Receive(fixture.Sender, block);
        bool forwardedWhilePending = fixture.SentToNeighbor(block);
        int pending = fixture.Pubsub.PendingValidationCount;
        bool applied = fixture.Raised.Single().Complete(validity);
        fixture.Receive(fixture.Neighbor, block);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((forwardedWhilePending, pending), Is.EqualTo((false, 1)), "the router holds a deferred message");
        Assert.That(applied, Is.True);
        Assert.That(fixture.SentToNeighbor(block), Is.EqualTo(validity == MessageValidity.Accepted), "only an accepted message is sent on");
        Assert.That(fixture.Router.GetDropCount(GossipDropReason.Duplicate), Is.Zero, "the router caches the id of a message with a verdict, so a copy is not validated again");
        Assert.That((fixture.Pubsub.PendingValidationCount, fixture.Validation.PendingBytes), Is.EqualTo((0, 0L)), "a given verdict releases its message");
    }

    /// <summary>
    /// Past the bound on messages or bytes awaiting a verdict a message is throttled: it is not validated, cached or forwarded, so the same
    /// message is validated once the bound frees.
    /// </summary>
    /// <remarks>phase0 p2p-interface.md "Topics and messages": clients SHOULD maintain maximum queue sizes to avoid DoS vectors.</remarks>
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

    /// <summary>
    /// One delivering peer holds at most its share of the messages awaiting a verdict, so cheap unsigned messages from it cannot throttle the
    /// gossip of every other peer; votes are not counted, as the vote queue bounds them.
    /// </summary>
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
        Assert.That(fixture.Validation.Verify(fixture.Sender, vote), Is.EqualTo(MessageValidity.Deferred), "a vote is not held to the peer's share");
    }

    /// <summary>
    /// A batched IWANT answer carrying many columns and a block from one peer is not held to the peer's share, as each column is checked as
    /// it is dispatched; only the block counts against it.
    /// </summary>
    [Test]
    public async Task One_rpc_of_many_columns_from_one_peer_is_not_held_to_its_share()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 128, maxPendingBytes: 1 << 24, TimeSpan.FromSeconds(30));
        byte[] digest = ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot));
        MessageValidity[] columns = [.. Enumerable.Range(0, 24).Select(subnet => fixture.Validation.Verify(fixture.Sender,
            new Message { Topic = GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName((ulong)subnet)), Data = ByteString.CopyFrom([(byte)subnet]) }))];

        MessageValidity block = fixture.Validation.Verify(fixture.Sender, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture.Validation.MaxPendingPerSource, Is.LessThan(columns.Length), "fixture: more columns than the peer's share");
        Assert.That(columns, Is.All.EqualTo(MessageValidity.Deferred));
        Assert.That(block, Is.EqualTo(MessageValidity.Deferred));
    }

    /// <summary>
    /// Aggregates and payload attestations are reserved apart, up to the vote queue: past it they are throttled, and a full vote bound
    /// leaves the room for blocks, columns and envelopes untouched. Every message is reserved before the router dispatches it.
    /// </summary>
    [Test]
    public async Task Votes_are_held_to_their_own_bound_and_leave_the_room_of_other_gossip()
    {
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), maxPendingVotes: 2);
        string aggregates = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof);
        MessageValidity[] votes = [.. Enumerable.Range(0, 3).Select(i => fixture.Validation.Verify(fixture.Sender, new Message { Topic = aggregates, Data = ByteString.CopyFrom([(byte)i]) }))];

        MessageValidity block = fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(votes, Is.EqualTo(new[] { MessageValidity.Deferred, MessageValidity.Deferred, MessageValidity.Throttled }));
        Assert.That(block, Is.EqualTo(MessageValidity.Deferred));
        Assert.That((fixture.Validation.PendingVotes, fixture.Validation.Pending), Is.EqualTo((2, 1)), "each deferred message is reserved before it is dispatched");
    }

    /// <summary>
    /// A verdict not given within the timeout is abandoned without caching the message's id, so another copy is validated again, and a
    /// verdict given after that neither forwards the message nor charges its sender.
    /// </summary>
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
        // The router drops the pending message when the validation task ends, just after the verdict is abandoned.
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

    /// <summary>
    /// A block for the next slot that arrives early is ignored at once, so no verdict waits a slot before any signature is checked, and the
    /// held block is still raised for import once its slot starts.
    /// </summary>
    /// <remarks>phase0 p2p-interface.md beacon_block: [IGNORE] a future slot, which a client MAY queue.</remarks>
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

    /// <summary>
    /// A message its consumer refuses for local load is checked again when a copy arrives, as the pubsub router keeps no id for it, also when
    /// the refusal comes once a block held for its slot is raised.
    /// </summary>
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

    /// <summary>
    /// The room reserved for a message the router never dispatches, as one it skips once it expired while an earlier message of its RPC was
    /// still being checked, is released when the reservation expires, so it is not lost to later gossip.
    /// </summary>
    [Test]
    public async Task Reservation_of_a_message_never_dispatched_is_released_when_it_expires()
    {
        SteppedTime time = new(DateTimeOffset.UnixEpoch.AddDays(20_000));
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: 16, maxPendingBytes: 1 << 20, TimeSpan.FromSeconds(30), time: time);
        string aggregates = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconAggregateAndProof);
        fixture.Validation.Verify(fixture.Sender, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot)) });
        fixture.Validation.Verify(fixture.Sender, new Message { Topic = aggregates, Data = ByteString.CopyFrom([1]) });
        (int pending, int votes) = (fixture.Validation.Pending, fixture.Validation.PendingVotes);

        time.Now += TimeSpan.FromSeconds(29);
        fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot - 1)) });
        (int pendingBeforeExpiry, int votesBeforeExpiry) = (fixture.Validation.Pending, fixture.Validation.PendingVotes);
        time.Now += TimeSpan.FromSeconds(3);
        fixture.Validation.Verify(fixture.Neighbor, new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(BlockMessage(CurrentSlot - 2)) });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((pending, votes), Is.EqualTo((1, 1)), "fixture: both messages reserved and never dispatched");
        Assert.That((pendingBeforeExpiry, votesBeforeExpiry), Is.EqualTo((2, 1)), "a reservation lasts as long as the router may still dispatch its message");
        Assert.That((fixture.Validation.Pending, fixture.Validation.PendingVotes), Is.EqualTo((2, 0)), "the expired reservations are released, the later ones kept");
    }

    /// <summary>A clock a test moves by hand.</summary>
    private sealed class SteppedTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async Task AssertDeferredPeerPenaltyAsync(string name, byte[] payload, Func<GossipVerdict, Task> validate, MessageValidity expected)
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        await using BeaconP2P p2p = container.Resolve<BeaconP2P>();
        string topic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), name);
        using PubsubRouter pubsub = new(new PeerStore(), GossipScoring.Configure(p2p.PubsubSettingsForTest, [topic], Spec));
        pubsub.GetTopic(topic);
        (PeerId sender, _, Action<Rpc> receive) = ConnectSubscribedPeer(pubsub, topic);
        (PeerId neighbor, _, _) = ConnectSubscribedPeer(pubsub, topic);
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
        await validate(await pending.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await pubsub.Heartbeat();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(given, Is.EqualTo(expected), "the worker must distinguish invalid input from unavailable local data");
        Assert.That(routing.Mesh[topic].Contains(sender), Is.EqualTo(expected != MessageValidity.Rejected), "invalid-message scoring charges the delivering peer");
        Assert.That(routing.Mesh[topic], Does.Contain(neighbor), "a peer that did not deliver the invalid message keeps its score");
    }

    private static readonly string BlockTopic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconBlock);

    /// <summary>A pubsub router with the host's settings and deferred validation, a sending peer and a mesh neighbor, and the verdicts of the blocks it raised.</summary>
    private sealed class DeferredFixture : IAsyncDisposable
    {
        private readonly IContainer _container;
        private readonly List<Rpc> _sentToNeighbor;
        private readonly List<Rpc> _sentToSender;
        private readonly Action<Rpc> _fromSender;
        private readonly Action<Rpc> _fromNeighbor;

        private DeferredFixture(IContainer container, PubsubRouter pubsub, GossipRouter router, DeferredGossipValidation validation, List<GossipVerdict> raised,
            (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) sender, (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) neighbor)
        {
            _container = container;
            Pubsub = pubsub;
            Router = router;
            Validation = validation;
            Raised = raised;
            _fromSender = sender.Receive;
            _fromNeighbor = neighbor.Receive;
            _sentToNeighbor = neighbor.Sent;
            _sentToSender = sender.Sent;
            Sender = sender.Peer;
            Neighbor = neighbor.Peer;
        }

        public PubsubRouter Pubsub { get; }

        public GossipRouter Router { get; }

        public DeferredGossipValidation Validation { get; }

        public List<GossipVerdict> Raised { get; }

        public PeerId Sender { get; }

        public PeerId Neighbor { get; }

        public static async Task<DeferredFixture> Create(int maxPending, long maxPendingBytes, TimeSpan timeout, string protocol = PubsubRouter.GossipsubProtocolVersionV11, int maxPendingVotes = 1024,
            TimeProvider? time = null)
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
            DeferredGossipValidation validation = new(pubsub, new GossipMessageValidator(router, new ColumnGossipRouter(Spec, clock, LimboLogs.Instance), Spec, clock),
                maxPending, maxPendingBytes, maxPendingVotes, timeout, LimboLogs.Instance.GetClassLogger<GossipRouterTests>(), CancellationToken.None, time);
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

        public void ReceiveRpc(PeerId from, Rpc rpc) => (from == Sender ? _fromSender : _fromNeighbor)(rpc);

        public IReadOnlyList<Rpc> SentTo(PeerId peer) => peer == Sender ? _sentToSender : _sentToNeighbor;

        public IEnumerable<MessageId> IdontwantsTo(PeerId peer) =>
            SentTo(peer).SelectMany(static rpc => rpc.Control?.Idontwant ?? []).SelectMany(static idontwant => idontwant.MessageIDs).Select(static id => new MessageId(id.ToByteArray()));

        public bool SentToNeighbor(byte[] data) =>
            _sentToNeighbor.SelectMany(static rpc => rpc.Publish).Any(message => message.Topic == BlockTopic && message.Data.Span.SequenceEqual(data));

        public ValueTask DisposeAsync()
        {
            Pubsub.Dispose();
            _container.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
