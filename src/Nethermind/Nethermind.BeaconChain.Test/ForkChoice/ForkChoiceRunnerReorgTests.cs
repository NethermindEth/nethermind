// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// <see cref="ForkChoiceRunner.GetProposerHead"/> against the phase0 and Fulu <c>get_proposer_head</c> helpers, on a
/// hand-built chain (<see cref="UnsignedChain"/>). No fork_choice vector reaches the proposer-equivocation branch, the
/// equivocation term of <c>is_head_weak</c>, the boost-free <c>get_attestation_score</c>, or a weak parent.
/// </summary>
/// <remarks>
/// The anchor registry is 16 validators of 32 ETH, so a committee's share is 16 ETH: a head is weak below 3.2 ETH
/// (no votes), a parent is strong above 25.6 ETH (one vote), and the boost is 6.4 ETH. Every block is proposed by
/// validator 0 unless a case overrides it, and only odd slots have a (one-member) committee.
/// </remarks>
public class ForkChoiceRunnerReorgTests
{
    /// <summary>
    /// A timely, weak head at slot 1 is re-orged for a slot-2 proposal only when another block of the same slot and
    /// proposer is in the store. The head is timely, so only the equivocation branch can return the parent.
    /// </summary>
    [TestCase(1ul, 0ul, 2ul, true, TestName = "same_slot_and_proposer_reorgs_the_head")]
    [TestCase(null, 0ul, 2ul, false, TestName = "single_block_is_kept")]
    [TestCase(2ul, 0ul, 2ul, false, TestName = "same_proposer_in_another_slot_is_not_an_equivocation")]
    [TestCase(1ul, 1ul, 2ul, false, TestName = "other_proposer_in_the_same_slot_is_not_an_equivocation")]
    [TestCase(1ul, 0ul, 3ul, false, TestName = "head_not_in_the_slot_before_the_proposal_is_kept")]
    public void Proposer_equivocation_reorgs_a_weak_head_in_the_previous_slot(ulong? secondBlockSlot, ulong secondBlockProposer, ulong proposalSlot, bool expectReorg)
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = CreateRunner(chain);
        UnsignedChain.ChainBlock head = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        TickTo(runner, slot: 1);
        Import(runner, head);
        if (secondBlockSlot == 1) Import(runner, WithProposer(chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xb1), secondBlockProposer));
        TickTo(runner, slot: 2);
        if (secondBlockSlot == 2) Import(runner, WithProposer(chain.Extend(chain.AnchorRoot, slot: 2, payloadHashByte: 0xb2), secondBlockProposer));

        Assert.That(runner.GetProposerHead(head.Root, proposalSlot), Is.EqualTo(expectReorg ? chain.AnchorRoot : head.Root));
    }

    /// <summary>
    /// <c>is_head_weak</c> adds the justified balance of each equivocating validator in the head slot's committees, so
    /// that more attestations can only make the head stronger. Slashing the slot-1 committee member makes the
    /// equivocating head strong enough to keep; slashing the slot-3 member does not, because it is not in the head
    /// slot's committee.
    /// </summary>
    [TestCase(1ul, false, TestName = "equivocator_in_the_head_slot_committee_counts_for_the_head")]
    [TestCase(3ul, true, TestName = "equivocator_in_another_slot_committee_does_not_count")]
    public void Equivocating_committee_members_count_towards_the_head_weight(ulong slashedCommitteeSlot, bool expectReorg)
    {
        (UnsignedChain chain, ForkChoiceRunner runner, UnsignedChain.ChainBlock a, UnsignedChain.ChainBlock b) = EquivocatingHeadAtSlotTwo();
        ulong[] slashed = chain.Committee(slashedCommitteeSlot);
        Assert.That(slashed, Is.Not.Empty, "fixture bug: the slashed committee must have a member");
        runner.OnAttesterSlashing(chain.DoubleVote(slashed, slot: 1, a.Root, b.Root), verifySignatures: false);

        Assert.That(runner.GetProposerHead(a.Root, proposalSlot: 2), Is.EqualTo(expectReorg ? chain.AnchorRoot : a.Root));
    }

    /// <summary>
    /// <c>is_head_weak</c> reads <c>get_attestation_score</c>, which has no proposer boost. A timely slot-2 child of the
    /// head holds the boost, and the proto-array adds it to every ancestor's weight; with it counted, the vote-less
    /// head would look strong enough (6.4 ETH over 3.2 ETH) to keep.
    /// </summary>
    [Test]
    public void Proposer_boost_on_a_descendant_does_not_strengthen_the_head()
    {
        (UnsignedChain chain, ForkChoiceRunner runner, UnsignedChain.ChainBlock a, _) = EquivocatingHeadAtSlotTwo();
        UnsignedChain.ChainBlock child = chain.Extend(a.Root, slot: 2, payloadHashByte: 0xc2);
        Import(runner, child);
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(child.Root), "fixture bug: the child must hold the boost");

        Assert.That(runner.GetProposerHead(a.Root, proposalSlot: 2), Is.EqualTo(chain.AnchorRoot));
    }

    /// <summary>
    /// The proto-array never boosts an execution-invalid block, so <c>get_attestation_score</c> must not subtract a
    /// boost from the head when the boosted descendant is invalidated after import.
    /// </summary>
    [Test]
    public void Invalidated_boost_root_is_not_subtracted_from_the_head()
    {
        (UnsignedChain chain, ForkChoiceRunner runner, UnsignedChain.ChainBlock a, _) = EquivocatingHeadAtSlotTwo();
        UnsignedChain.ChainBlock child = chain.Extend(a.Root, slot: 2, payloadHashByte: 0xc2);
        Import(runner, child, ExecutionStatus.Optimistic);
        Assert.That(runner.ProposerBoostRoot, Is.EqualTo(child.Root), "fixture bug: the child must hold the boost");
        runner.OnInvalidExecutionPayload(child.Root);

        Assert.That(runner.GetProposerHead(a.Root, proposalSlot: 2), Is.EqualTo(chain.AnchorRoot));
    }

    /// <summary>
    /// A block that <c>on_block</c> refuses is not in <c>store.blocks</c>, so it cannot make a second proposal of its
    /// slot: the timely head then has no equivocation and is kept. The refused block either carries an execution
    /// block hash with an irrelevant status, or builds on a parent whose payload was invalidated.
    /// </summary>
    [Test]
    public void Refused_block_is_not_a_second_proposal([Values] bool onInvalidParent)
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = CreateRunner(chain);
        UnsignedChain.ChainBlock invalid = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xb1);
        UnsignedChain.ChainBlock head = chain.Extend(chain.AnchorRoot, slot: 2, payloadHashByte: 0xa2);
        UnsignedChain.ChainBlock refused = chain.Extend(onInvalidParent ? invalid.Root : chain.AnchorRoot, slot: 2, payloadHashByte: 0xb2);
        TickTo(runner, slot: 1);
        Import(runner, invalid, ExecutionStatus.Optimistic);
        runner.OnInvalidExecutionPayload(invalid.Root);
        TickTo(runner, slot: 2);
        Import(runner, head);
        Assert.That(() => runner.OnBlock(refused.Block, refused.PostState, onInvalidParent ? ExecutionStatus.Valid : ExecutionStatus.Irrelevant, (IReadOnlyList<DataColumnSidecar>?)null),
            Throws.Exception, "fixture bug: the block must be refused");
        TickTo(runner, slot: 3);

        Assert.That(runner.GetProposerHead(head.Root, proposalSlot: 3), Is.EqualTo(head.Root));
    }

    /// <summary>
    /// A block that <c>on_block</c> refuses leaves no node or second proposal and cannot take the proposer boost.
    /// A failed invalidation leaves D optimistic under invalid B, so valid E on D is refused as its validity propagates to B.
    /// </summary>
    [TestCase(true, TestName = "Block_refused_by_on_block_leaves_no_node_and_is_not_a_second_proposal")]
    [TestCase(false, TestName = "Timely_block_refused_by_on_block_does_not_take_the_proposer_boost")]
    public void Refused_block_preserves_the_slot_proposal_and_boost(bool withAcceptedHead)
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = CreateRunner(chain);
        UnsignedChain.ChainBlock a = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock b = chain.Extend(a.Root, slot: 2, payloadHashByte: 0xa2);
        UnsignedChain.ChainBlock c = chain.Extend(b.Root, slot: 3, payloadHashByte: 0xa3);
        UnsignedChain.ChainBlock d = chain.Extend(b.Root, slot: 3, payloadHashByte: 0xb3);
        UnsignedChain.ChainBlock? head = withAcceptedHead ? chain.Extend(a.Root, slot: 4, payloadHashByte: 0xa4) : null;
        UnsignedChain.ChainBlock refused = chain.Extend(d.Root, slot: 4, payloadHashByte: 0xb4);
        TickTo(runner, slot: 1);
        Import(runner, a);
        TickTo(runner, slot: 2);
        Import(runner, b, ExecutionStatus.Optimistic);
        TickTo(runner, slot: 3);
        Import(runner, c, ExecutionStatus.Optimistic);
        Import(runner, d, ExecutionStatus.Optimistic);
        Assert.That(() => runner.OnInvalidExecutionPayload(c.Root, runner.GetExecutionBlockHash(chain.AnchorRoot)),
            Throws.TypeOf<ProtoArrayException>(), withAcceptedHead ? "fixture bug: the invalidation must stop at the valid A after invalidating B" : null);
        TickTo(runner, slot: 4);
        if (head is not null)
            Import(runner, head);

        Assert.That(() => Import(runner, refused), Throws.TypeOf<ProtoArrayException>(),
            withAcceptedHead ? "fixture bug: E must be refused" : "fixture: the proto-array refuses E");
        if (head is not null)
        {
            Assert.That(runner.ContainsBlock(refused.Root), Is.False, "a refused block leaves no node");
            TickTo(runner, slot: 5);
            Assert.That(runner.GetProposerHead(head.Root, proposalSlot: 5), Is.EqualTo(head.Root));
        }
        else
            Assert.That(runner.ProposerBoostRoot, Is.EqualTo(Hash256.Zero));
    }

    private abstract record LateHeadCase(string Name, bool Reorg);
    private sealed record ProposalTime(ulong Seconds, bool ParentVoted, bool Expected) : LateHeadCase($"Late head at proposal +{Seconds}s, parent voted {ParentVoted}", Expected);
    private sealed record ArrivalTime(ulong Seconds, bool Expected) : LateHeadCase($"Head arrives at +{Seconds}s", Expected);
    private sealed record EpochBoundary(string CaseName, ulong ParentSlot, ulong VoteSlot) : LateHeadCase(CaseName, true);
    private sealed record ParentStrength(string CaseName, long TotalOffsetGwei, bool Expected) : LateHeadCase(CaseName, Expected);
    private static readonly LateHeadCase[] LateHeadScenarios =
    [
        new ProposalTime(0, true, true),
        new ProposalTime(2, true, true),
        new ProposalTime(3, true, false),
        new ProposalTime(0, false, false),
        new ArrivalTime(3, false),
        new ArrivalTime(4, true),
        new EpochBoundary("proposal_in_the_last_slot_of_the_epoch", 29, 29),
        new EpochBoundary("proposal_in_the_first_slot_of_the_next_epoch", 30, 31),
        new ParentStrength("score_above_the_threshold_is_strong", -3200, true),
        new ParentStrength("score_equal_to_the_threshold_is_not_strong", 0, false),
        new ParentStrength("score_below_the_threshold_is_not_strong", 3200, false),
    ];
    private static IEnumerable<TestCaseData> LateHeadCases()
    {
        for (int i = 0; i < LateHeadScenarios.Length; i++) yield return new TestCaseData(i).SetName(LateHeadScenarios[i].Name);
    }

    /// <summary>Checks late-head reorg cutoffs with real votes and justified balances.</summary>
    /// <remarks>
    /// Arrival at 3 s is timely and 4 s is late; proposing at 2 s is allowed and 3 s is past the cutoff.
    /// specs/fulu/fork-choice.md (EIP-7917) permits reorg across an epoch boundary; slot 30 uses slot 31's committee because its own is empty.
    /// A parent's score must exceed its threshold strictly.
    /// </remarks>
    [TestCaseSource(nameof(LateHeadCases))]
    public void Late_head_reorg_preserves_arrival_proposal_epoch_and_parent_strength_boundaries(int index)
    {
        LateHeadCase test = LateHeadScenarios[index];
        UnsignedChain chain = UnsignedChain.Create();
        ulong parentSlot = test is EpochBoundary boundary ? boundary.ParentSlot : 1;
        ulong voteSlot = test is EpochBoundary epoch ? epoch.VoteSlot : 1;
        if (test is EpochBoundary) Assert.That(chain.Committee(voteSlot), Is.Not.Empty, "fixture bug: the parent's vote needs a committee member");
        IForkChoiceStateProvider? states = null;
        if (test is ParentStrength strength)
        {
            const ulong Gwei = 1_000_000_000;
            Validator[] registry = chain.Anchor.AnchorState.Validators!;
            Assert.That(registry, Has.Length.EqualTo(16).And.All.Property(nameof(Validator.EffectiveBalance)).EqualTo(32 * Gwei), "fixture bug: the registry must be 16 validators of 32 ETH");
            Assert.That(chain.Committee(1), Has.Length.EqualTo(1), "fixture bug: the parent's vote must be a single validator");
            states = new AnchorWithBallast(chain, (ulong)((long)(160 * Gwei) + strength.TotalOffsetGwei));
        }
        ForkChoiceRunner runner = CreateRunner(chain, states);
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, slot: parentSlot, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock head = chain.Extend(parent.Root, slot: parentSlot + 1, payloadHashByte: 0xa2);
        TickTo(runner, slot: parentSlot);
        Import(runner, parent);
        TickTo(runner, slot: parentSlot + 1, test is ArrivalTime arrival ? arrival.Seconds : 5);
        Import(runner, head);
        Hash256 boostRoot = runner.ProposerBoostRoot;
        TickTo(runner, slot: parentSlot + 2, test is ProposalTime proposal ? proposal.Seconds : 0);
        if (test is not ProposalTime { ParentVoted: false }) runner.OnAttestation(chain.Vote(voteSlot, parent.Root), verifySignature: false);
        using (Assert.EnterMultipleScope())
        {
            if (test is ArrivalTime) Assert.That(boostRoot, Is.EqualTo(test.Reorg ? Hash256.Zero : head.Root), "only a timely head is boosted");
            Assert.That(runner.GetHead(), Is.EqualTo(head.Root), "fixture bug: the block must be the head");
            Assert.That(runner.GetProposerHead(head.Root, proposalSlot: parentSlot + 2), Is.EqualTo(test.Reorg ? parent.Root : head.Root));
        }
    }
    /// <summary>Two timely slot-1 blocks A and B by the same proposer, with the clock at the start of slot 2 so neither holds the boost.</summary>
    private static (UnsignedChain Chain, ForkChoiceRunner Runner, UnsignedChain.ChainBlock A, UnsignedChain.ChainBlock B) EquivocatingHeadAtSlotTwo()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = CreateRunner(chain);
        UnsignedChain.ChainBlock a = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock b = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xb1);
        TickTo(runner, slot: 1);
        Import(runner, a);
        Import(runner, b);
        TickTo(runner, slot: 2);
        return (chain, runner, a, b);
    }

    /// <summary>Two forks that diverged before the lookahead can have different proposers for one slot; on_block does not check the proposer.</summary>
    private static UnsignedChain.ChainBlock WithProposer(UnsignedChain.ChainBlock block, ulong proposerIndex)
    {
        block.Block.Message!.ProposerIndex = proposerIndex;
        return block;
    }

    private static ForkChoiceRunner CreateRunner(UnsignedChain chain, IForkChoiceStateProvider? states = null) =>
        new(chain.Spec, chain.Anchor.AnchorState, chain.Anchor.AnchorBlock.Message!, states ?? chain, chain.Anchor.Pubkeys);

    private static void TickTo(ForkChoiceRunner runner, ulong slot, ulong secondsIntoSlot = 0) =>
        runner.OnTick(runner.GenesisTime + slot * Presets.SecondsPerSlot + secondsIntoSlot);

    private static void Import(ForkChoiceRunner runner, UnsignedChain.ChainBlock block, ExecutionStatus status = ExecutionStatus.Valid) =>
        runner.OnBlock(block.Block, block.PostState, status, (IReadOnlyList<DataColumnSidecar>?)null);

    /// <summary>
    /// The chain's states, except that the anchor (the justified state) gives one validator outside the slot-1 committee
    /// <paramref name="ballastBalance"/>. Effective balances do not move committees, so the chain's votes stay valid.
    /// </summary>
    private sealed class AnchorWithBallast(UnsignedChain chain, ulong ballastBalance) : IForkChoiceStateProvider
    {
        private readonly BeaconStateFulu _anchor = WithBallast(chain, ballastBalance);

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => blockRoot == chain.AnchorRoot ? _anchor : chain.GetBlockState(blockRoot);

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

        private static BeaconStateFulu WithBallast(UnsignedChain chain, ulong ballastBalance)
        {
            BeaconStateFulu state = chain.Anchor.AnchorState.Clone();
            int ballast = Array.IndexOf(chain.Committee(1), 0ul) < 0 ? 0 : 1;
            Validator validator = state.Validators![ballast].Clone();
            validator.EffectiveBalance = ballastBalance;
            state.Validators[ballast] = validator;
            return state;
        }
    }
}
