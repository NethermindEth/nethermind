// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
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

/// <summary>The attester slashing index rules and the Gloas same-slot vote rule that return a verdict without beacon state.</summary>
public partial class GossipRouterTests
{
    private static readonly ulong VoteSlot = FirstGloasSlot + 1;

    // phase0 attester_slashing: is_valid_indexed_attestation needs sorted, unique indices, and a REJECT follows the seen-index IGNORE.
    [TestCase(new ulong[] { 1, 2, 3 }, new ulong[] { 2, 3, 4 }, null, false, TestName = "slashing with sorted unique indices is consumed")]
    [TestCase(new ulong[] { 2, 1, 3 }, new ulong[] { 1, 2, 3 }, GossipDropReason.InvalidField, true, TestName = "slashing whose first attestation's indices are unsorted")]
    [TestCase(new ulong[] { 1, 2, 3 }, new ulong[] { 2, 2, 3 }, GossipDropReason.InvalidField, true, TestName = "slashing whose second attestation repeats an index")]
    [TestCase(new ulong[] { 2, 1 }, new ulong[] { 3, 4 }, GossipDropReason.InvalidField, false, TestName = "unsorted slashing with no common index only drops")]
    public void Attester_slashing_indices_must_be_sorted_and_unique(ulong[] indices1, ulong[] indices2, GossipDropReason? reason, bool rejected)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.AttesterSlashingReceived += _ => received++;
        IndexedAttestation first = CreateIndexedAttestation(1, 4);
        IndexedAttestation second = CreateIndexedAttestation(2, 3);
        first.AttestingIndices = indices1;
        second.AttestingIndices = indices2;

        MessageValidity validity = router.HandleAttesterSlashing(Snappy.CompressToArray(AttesterSlashing.Encode(new AttesterSlashing { Attestation1 = first, Attestation2 = second })));

        AssertVerdict(router, validity, received, reason, rejected);
    }

    // gloas is_valid_indexed_attestation (EIP-7688): at most MAX_VALIDATORS_PER_COMMITTEE * MAX_COMMITTEES_PER_SLOT attesting indices.
    [TestCase(0, null, TestName = "Gloas slashing at the attesting-index bound is consumed")]
    [TestCase(1, GossipDropReason.InvalidField, TestName = "Gloas slashing one index over the attesting-index bound")]
    public void Gloas_attester_slashing_indices_are_bounded(int overBound, GossipDropReason? reason)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.GloasAttesterSlashingReceived += _ => received++;
        ulong[] indices = [.. Enumerable.Range(0, Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot + overBound).Select(static i => (ulong)i)];
        AttesterSlashingGloas slashing = GossipMessageValidatorTests.GloasSlashing(indices, [0], secondSource: 2, secondTarget: 3);

        MessageValidity validity = router.Handle(GossipTopics.AttesterSlashing, gloasTopic: true, GossipMessageValidatorTests.Encode(slashing));

        AssertVerdict(router, validity, received, reason, rejected: reason is not null);
    }

    // gloas verify_attestation_payload_status: [REJECT] a vote at its block's own slot must have index 0.
    [TestCase(0L, 1UL, true, false, GossipDropReason.InvalidField, true, TestName = "Gloas aggregate with index 1 at its held block's slot")]
    [TestCase(0L, 0UL, true, false, null, false, TestName = "Gloas aggregate with index 0 at its held block's slot is consumed")]
    [TestCase(1L, 1UL, true, false, null, false, TestName = "Gloas aggregate with index 1 a slot after its held block is consumed")]
    [TestCase(0L, 1UL, false, false, null, false, TestName = "Gloas aggregate with index 1 for a block not held is consumed")]
    [TestCase(0L, 1UL, true, true, GossipDropReason.InvalidField, false, TestName = "early next-slot Gloas aggregate with index 1 at its held block's slot only drops")]
    public void Gloas_aggregate_at_its_held_block_slot_must_have_index_zero(long slotsAfterBlock, ulong index, bool held, bool earlyNextSlot, GossipDropReason? reason, bool rejected)
    {
        (GossipRouter router, BeaconChainStore store) = CreateSepoliaRouterWithStore();
        int received = 0;
        router.GloasAggregateAndProofReceived += _ => received++;
        ulong voteSlot = earlyNextSlot ? VoteSlot + 1 : VoteSlot;
        Hash256 blockRoot = Keccak.Compute("voted block");
        if (held) store.PutForkedBlock(blockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock((ulong)((long)voteSlot - slotsAfterBlock))));

        MessageValidity validity = router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(VoteFor(blockRoot, voteSlot, index)));

        AssertVerdict(router, validity, received, reason, rejected);
    }

    // A voted block's slot costs a store decode on the network thread, so reads are bounded per slot and a read slot is kept.
    [Test]
    public void Voted_block_slot_reads_past_their_budget_skip_the_same_slot_rule_but_a_read_block_still_applies_it()
    {
        (GossipRouter router, BeaconChainStore store) = CreateSepoliaRouterWithStore();
        int received = 0;
        router.GloasAggregateAndProofReceived += _ => received++;
        Hash256[] roots = [.. Enumerable.Range(0, GossipRouter.VotedBlockSlotReadsPerSlot + 1).Select(static i => Keccak.Compute($"voted block {i}"))];
        foreach (Hash256 root in roots)
        {
            store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(VoteSlot)));
        }

        MessageValidity[] verdicts = [.. roots.Select(root => router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(VoteFor(root, VoteSlot, 1))))];
        SignedAggregateAndProofGloas repeat = VoteFor(roots[0], VoteSlot, 1);
        repeat.Message!.AggregatorIndex++;
        MessageValidity cached = router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(repeat));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdicts[..^1], Is.All.EqualTo(MessageValidity.Rejected), "within the budget");
            Assert.That((verdicts[^1], received), Is.EqualTo((MessageValidity.Ignored, 1)), "past the budget the aggregate is consumed for fork choice");
            Assert.That(cached, Is.EqualTo(MessageValidity.Rejected), "a block already read needs no decode");
        }
    }

    // Aggregates arrive many per slot, so a voted block's cached slot must answer the rule without a store lookup.
    [Test]
    public void Voted_block_slot_once_read_applies_the_same_slot_rule_without_a_store_lookup()
    {
        (GossipRouter router, BeaconChainStore store) = CreateSepoliaRouterWithStore();
        Hash256 root = Keccak.Compute("pruned voted block");
        store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(VoteSlot)));
        MessageValidity read = router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(VoteFor(root, VoteSlot, 1)));
        store.DeleteBlock(root);
        SignedAggregateAndProofGloas repeat = VoteFor(root, VoteSlot, 1);
        repeat.Message!.AggregatorIndex++;

        MessageValidity cached = router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(repeat));

        Assert.That((read, cached, store.HasBlock(root)), Is.EqualTo((MessageValidity.Rejected, MessageValidity.Rejected, false)));
    }

    // A vote naming a block not held costs a key lookup, not a store decode, so such votes cannot spend the budget of held ones.
    [Test]
    public void Votes_for_blocks_not_held_leave_the_voted_block_budget_whole()
    {
        (GossipRouter router, BeaconChainStore store) = CreateSepoliaRouterWithStore();
        int received = 0;
        router.GloasAggregateAndProofReceived += _ => received++;
        Hash256 heldRoot = Keccak.Compute("held voted block");
        store.PutForkedBlock(heldRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(VoteSlot)));

        MessageValidity[] unheld = [.. Enumerable.Range(0, GossipRouter.VotedBlockSlotReadsPerSlot + 1)
            .Select(i => router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(VoteFor(Keccak.Compute($"unheld block {i}"), VoteSlot, 1))))];
        MessageValidity held = router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(VoteFor(heldRoot, VoteSlot, 1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That((unheld, received), Is.EqualTo((Enumerable.Repeat(MessageValidity.Ignored, GossipRouter.VotedBlockSlotReadsPerSlot + 1).ToArray(), GossipRouter.VotedBlockSlotReadsPerSlot + 1)), "votes for blocks not held are consumed for fork choice");
            Assert.That(held, Is.EqualTo(MessageValidity.Rejected), "the held block's slot is still read");
        }
    }

    public enum StoreReadRule { ParentSlot, VotedBlockSlot, EnvelopeBlock }

    // Each rule reading held blocks from the store decodes within its own per-slot budget, so spending one leaves the others applied.
    [TestCase(StoreReadRule.VotedBlockSlot, StoreReadRule.ParentSlot)]
    [TestCase(StoreReadRule.VotedBlockSlot, StoreReadRule.EnvelopeBlock)]
    [TestCase(StoreReadRule.ParentSlot, StoreReadRule.VotedBlockSlot)]
    [TestCase(StoreReadRule.EnvelopeBlock, StoreReadRule.VotedBlockSlot)]
    public void Store_read_rules_have_separate_budgets(StoreReadRule spent, StoreReadRule probed)
    {
        EnvelopeFixture fixture = new(withStore: true);
        ulong slot = EnvelopeFixture.WallSlot;
        int budget = spent switch
        {
            StoreReadRule.ParentSlot => GossipRouter.ParentSlotReadsPerSlot,
            StoreReadRule.VotedBlockSlot => GossipRouter.VotedBlockSlotReadsPerSlot,
            _ => GossipRouter.StoreDecodesPerSlot,
        };

        MessageValidity[] spending = [.. Enumerable.Range(0, budget).Select(i => Apply(spent, fixture.PutGloasBlock(slot, parentRoot: Keccak.Compute($"spent block parent {i}"))))];
        MessageValidity probe = Apply(probed, fixture.PutGloasBlock(slot, parentRoot: Keccak.Compute("probed block parent")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spending, Is.All.EqualTo(MessageValidity.Rejected), "fixture: every spending message reads a held block and is rejected by its rule");
            Assert.That(probe, Is.EqualTo(MessageValidity.Rejected), "the probed rule's budget is still whole");
            Assert.That(fixture.Router.GetDropCount(GossipDropReason.StoreDecodeBudgetSpent), Is.Zero);
        }

        MessageValidity Apply(StoreReadRule rule, Hash256 heldRoot) => rule switch
        {
            StoreReadRule.ParentSlot => fixture.Router.Handle(GossipTopics.BeaconBlock, gloasTopic: true, Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(CreateMinimalGloasBlock(slot, heldRoot)))),
            StoreReadRule.VotedBlockSlot => fixture.Router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(VoteFor(heldRoot, slot, 1))),
            _ => fixture.Handle(fixture.Envelope(heldRoot, builderIndex: EnvelopeFixture.BuilderIndex + 1)),
        };
    }

    private static SignedAggregateAndProofGloas VoteFor(Hash256 blockRoot, ulong slot, ulong index)
    {
        SignedAggregateAndProofGloas aggregate = GossipMessageValidatorTests.GloasAggregate(slot, index);
        aggregate.Message!.Aggregate!.Data!.BeaconBlockRoot = blockRoot;
        return aggregate;
    }

    private static (GossipRouter Router, BeaconChainStore Store) CreateSepoliaRouterWithStore()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        return (new GossipRouter(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(SepoliaSlotStart(VoteSlot).AddSeconds(6))), LimboLogs.Instance, store), store);
    }
}
