// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

// fulu/p2p-interface.md data_column_sidecar_{subnet_id}: [REJECT] the sidecar is from a higher slot than its parent; the store holds only accepted blocks.
public class ColumnGossipRouterParentSlotTests
{
    private const ulong CurrentSlot = 13_410_304;
    private const ulong Subnet = 5;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [TestCase(0UL, MessageValidity.Rejected, TestName = "Fulu sidecar at its held parent's slot is rejected")]
    [TestCase(1UL, MessageValidity.Ignored, TestName = "Fulu sidecar one slot above its held parent is consumed")]
    public void Fulu_sidecar_slot_must_be_above_its_held_parent(ulong slotsAboveParent, MessageValidity expected)
    {
        (ColumnGossipRouter router, BeaconChainStore store, _) = Create();
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Subnet, CurrentSlot);
        Hash256 parent = Hold(store, CurrentSlot - slotsAboveParent, proposer: 0);
        int received = 0;
        router.DataColumnSidecarReceived += _ => received++;

        MessageValidity validity = router.Handle(Subnet, gloasTopic: false, Message(sidecar, parent, proposer: 0));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(expected));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.NotAboveParentSlot), Is.EqualTo(expected == MessageValidity.Rejected ? 1 : 0));
            Assert.That(received, Is.EqualTo(expected == MessageValidity.Rejected ? 0 : 1));
        }
    }

    [Test]
    public void Parent_slot_decodes_are_bounded_per_slot_and_renewed_at_the_next()
    {
        (ColumnGossipRouter router, BeaconChainStore store, ManualTimestamper clock) = Create();
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Subnet, CurrentSlot);
        int budget = ColumnGossipRouter.ParentSlotReadsPerSlot;
        Hash256[] parents = new Hash256[budget + 1];
        for (int i = 0; i < parents.Length; i++)
        {
            parents[i] = Hold(store, CurrentSlot, proposer: (ulong)i);
        }

        MessageValidity[] verdicts = new MessageValidity[parents.Length];
        for (int i = 0; i < parents.Length; i++)
        {
            verdicts[i] = router.Handle(Subnet, gloasTopic: false, Message(sidecar, parents[i], proposer: (ulong)i));
        }

        // A decoded parent's slot is cached, so it still convicts once the budget is spent.
        MessageValidity cachedAfterSpent = router.Handle(Subnet, gloasTopic: false, Message(sidecar, parents[0], proposer: 1000));

        clock.UtcNow = clock.UtcNow.AddSeconds(Spec.SecondsPerSlot);
        MessageValidity nextSlot = router.Handle(Subnet, gloasTopic: false, Message(sidecar, parents[budget], proposer: 1001));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdicts[..budget], Is.All.EqualTo(MessageValidity.Rejected));
            Assert.That(verdicts[budget], Is.EqualTo(MessageValidity.Ignored), "past the budget the parent is not decoded and the rule is skipped");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.NotAboveParentSlot), Is.EqualTo(budget + 2));
            Assert.That(cachedAfterSpent, Is.EqualTo(MessageValidity.Rejected));
            Assert.That(nextSlot, Is.EqualTo(MessageValidity.Rejected));
        }
    }

    // Every column of a block names the same parent, so its cached slot must answer the rule without a store lookup.
    [Test]
    public void Parent_slot_once_read_applies_the_rule_without_a_store_lookup()
    {
        (ColumnGossipRouter router, BeaconChainStore store, _) = Create();
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Subnet, CurrentSlot);
        Hash256 parent = Hold(store, CurrentSlot, proposer: 0);
        MessageValidity read = router.Handle(Subnet, gloasTopic: false, Message(sidecar, parent, proposer: 0));
        store.DeleteBlock(parent);

        MessageValidity cached = router.Handle(Subnet, gloasTopic: false, Message(sidecar, parent, proposer: 1));

        Assert.That((read, cached, store.HasBlock(parent)), Is.EqualTo((MessageValidity.Rejected, MessageValidity.Rejected, false)));
    }

    private static (ColumnGossipRouter Router, BeaconChainStore Store, ManualTimestamper Clock) Create()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 6));
        ColumnGossipRouter router = new(Spec, new SlotClock(Spec, clock), LimboLogs.Instance, store: store);
        router.Start(_ => new NullTopic(), ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), [Subnet]);
        return (router, store, clock);
    }

    private static Hash256 Hold(BeaconChainStore store, ulong slot, ulong proposer)
    {
        SignedBeaconBlock block = CreateMinimalBlock(slot);
        block.Message!.ProposerIndex = proposer;
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfFulu(block));
        return root;
    }

    // The inclusion proof binds the commitments to the header's body root only, so parent and proposer can be rewritten.
    private static byte[] Message(DataColumnSidecar sidecar, Hash256 parent, ulong proposer)
    {
        sidecar.SignedBlockHeader!.Message!.ParentRoot = parent;
        sidecar.SignedBlockHeader.Message.ProposerIndex = proposer;
        return Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar));
    }

    private sealed class NullTopic : ITopic
    {
        public event Action<byte[]>? OnMessage { add { } remove { } }

        public bool IsSubscribed => true;

        public void Subscribe() { }

        public void Unsubscribe() { }

        public void Publish(byte[] value) { }

        public void Publish(Google.Protobuf.IMessage value) { }
    }
}
