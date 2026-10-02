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

        using (Assert.EnterMultipleScope())
        {
            Assert.That((forwardedWhilePending, pending), Is.EqualTo((false, 1)), "the router holds a deferred message");
            Assert.That(applied, Is.True);
            Assert.That(fixture.SentToNeighbor(block), Is.EqualTo(validity == MessageValidity.Accepted), "only an accepted message is sent on");
            Assert.That(fixture.Router.GetDropCount(GossipDropReason.Duplicate), Is.Zero, "the router caches the id of a message with a verdict, so a copy is not validated again");
            Assert.That((fixture.Pubsub.PendingValidationCount, fixture.Validation.PendingBytes), Is.EqualTo((0, 0L)), "a given verdict releases its message");
        }
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
        await using DeferredFixture fixture = await DeferredFixture.Create(maxPending: byBytes ? 8 : 1, maxPendingBytes: byBytes ? firstSize + 1 : 1 << 20, TimeSpan.FromSeconds(30));
        ulong throttledBefore = Metrics.BeaconChainGossipThrottled;

        fixture.Receive(fixture.Sender, first);
        fixture.Receive(fixture.Sender, second);
        int raisedWhileFull = fixture.Raised.Count;
        fixture.Raised[0].Complete(MessageValidity.Ignored);
        fixture.Receive(fixture.Sender, second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raisedWhileFull, Is.EqualTo(1), "the message past the bound is not validated");
            Assert.That(Metrics.BeaconChainGossipThrottled - throttledBefore, Is.EqualTo(1UL), "the throttled message is counted");
            Assert.That(fixture.Raised, Has.Count.EqualTo(2), "the throttled message is validated once the bound frees");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lateApplied, Is.False);
            Assert.That(fixture.SentToNeighbor(block), Is.False, "a late verdict forwards nothing");
            Assert.That((pendingAfter, fixture.Validation.PendingBytes), Is.EqualTo((0, 0L)));
            Assert.That(Metrics.BeaconChainGossipVerdictsAbandoned - abandonedBefore, Is.EqualTo(1UL));
            Assert.That(Metrics.BeaconChainGossipVerdictsLate - lateBefore, Is.EqualTo(1UL));
            Assert.That(fixture.Router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(1), "the router validated the copy again, as it cached no id for the abandoned message");
        }
    }

    /// <summary>A block for the next slot that arrives early is held with its verdict, and the verdict is handed on with the block once its slot starts.</summary>
    /// <remarks>phase0 p2p-interface.md beacon_block: [IGNORE] a future slot, which a client MAY queue; the router does not redeliver an ignored id.</remarks>
    [Test]
    public void Early_next_slot_block_keeps_its_verdict_until_its_slot_starts()
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That((validity, handedOff, raisedEarly), Is.EqualTo((MessageValidity.Ignored, true, 0)), "the held block's verdict is not given at once");
            Assert.That(raised.SingleOrDefault(), Is.SameAs(verdict), "the block is raised with its own verdict once its slot starts");
        }
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

    private static readonly string BlockTopic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.BeaconBlock);

    /// <summary>A pubsub router with the host's settings and deferred validation, a sending peer and a mesh neighbor, and the verdicts of the blocks it raised.</summary>
    private sealed class DeferredFixture : IAsyncDisposable
    {
        private readonly IContainer _container;
        private readonly List<Rpc> _sentToNeighbor;
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
            Sender = sender.Peer;
            Neighbor = neighbor.Peer;
        }

        public PubsubRouter Pubsub { get; }

        public GossipRouter Router { get; }

        public DeferredGossipValidation Validation { get; }

        public List<GossipVerdict> Raised { get; }

        public PeerId Sender { get; }

        public PeerId Neighbor { get; }

        public static async Task<DeferredFixture> Create(int maxPending, long maxPendingBytes, TimeSpan timeout)
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
                maxPending, maxPendingBytes, timeout, LimboLogs.Instance.GetClassLogger<GossipRouterTests>(), CancellationToken.None);
            pubsub.VerifyMessage = validation.Verify;
            pubsub.OnDeferredMessage = validation.ValidateAsync;
            pubsub.GetTopic(BlockTopic);
            (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) sender = ConnectSubscribedPeer(pubsub, BlockTopic);
            (PeerId Peer, List<Rpc> Sent, Action<Rpc> Receive) neighbor = ConnectSubscribedPeer(pubsub, BlockTopic);
            ((IRoutingStateContainer)pubsub).Mesh[BlockTopic].UnionWith([sender.Peer, neighbor.Peer]);
            return new DeferredFixture(container, pubsub, router, validation, raised, sender, neighbor);
        }

        public void Receive(PeerId from, byte[] data)
        {
            Rpc rpc = new();
            rpc.Publish.Add(new Message { Topic = BlockTopic, Data = ByteString.CopyFrom(data) });
            (from == Sender ? _fromSender : _fromNeighbor)(rpc);
        }

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
