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
    /// A block that <c>on_block</c> throws on after the proto-array inserted its node is still refused, so it cannot
    /// make a second proposal of its slot either. A failed invalidation (the execution layer calls a valid block
    /// invalid) leaves D optimistic under the invalid B, so a valid block E on D is inserted and then refused while
    /// its validity is propagated up to B.
    /// </summary>
    [Test]
    public void Block_refused_after_its_node_is_inserted_is_not_a_second_proposal()
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = CreateRunner(chain);
        UnsignedChain.ChainBlock a = chain.Extend(chain.AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock b = chain.Extend(a.Root, slot: 2, payloadHashByte: 0xa2);
        UnsignedChain.ChainBlock c = chain.Extend(b.Root, slot: 3, payloadHashByte: 0xa3);
        UnsignedChain.ChainBlock d = chain.Extend(b.Root, slot: 3, payloadHashByte: 0xb3);
        UnsignedChain.ChainBlock head = chain.Extend(a.Root, slot: 4, payloadHashByte: 0xa4);
        UnsignedChain.ChainBlock refused = chain.Extend(d.Root, slot: 4, payloadHashByte: 0xb4);
        TickTo(runner, slot: 1);
        Import(runner, a);
        TickTo(runner, slot: 2);
        Import(runner, b, ExecutionStatus.Optimistic);
        TickTo(runner, slot: 3);
        Import(runner, c, ExecutionStatus.Optimistic);
        Import(runner, d, ExecutionStatus.Optimistic);
        Assert.That(() => runner.OnInvalidExecutionPayload(c.Root, runner.GetExecutionBlockHash(chain.AnchorRoot)),
            Throws.TypeOf<ProtoArrayException>(), "fixture bug: the invalidation must stop at the valid A after invalidating B");
        TickTo(runner, slot: 4);
        Import(runner, head);
        Assert.That(() => Import(runner, refused), Throws.TypeOf<ProtoArrayException>(), "fixture bug: E must be refused");
        Assert.That(runner.ContainsBlock(refused.Root), Is.True, "fixture bug: E's node must be inserted before the refusal");
        TickTo(runner, slot: 5);

        Assert.That(runner.GetProposerHead(head.Root, proposalSlot: 5), Is.EqualTo(head.Root));
    }

    /// <summary>
    /// The late-head branch: a late, vote-less slot-2 head on a slot-1 parent is re-orged for a slot-3 proposal only
    /// when <c>is_parent_strong</c> holds (the parent has one vote) and the clock is at most
    /// <c>get_proposer_reorg_cutoff_ms</c> (2000 ms of a 12 s slot) into the slot.
    /// </summary>
    [TestCase(0ul, true, true)]
    [TestCase(2ul, true, true)]
    [TestCase(3ul, true, false)]
    [TestCase(0ul, false, false)]
    public void Late_weak_head_is_reorged_only_while_proposing_on_time(ulong secondsIntoSlot, bool parentVoted, bool expectReorg)
    {
        UnsignedChain chain = UnsignedChain.Create();
        ForkChoiceRunner runner = CreateRunner(chain);
        (UnsignedChain.ChainBlock parent, UnsignedChain.ChainBlock head) = LateHeadOnParent(chain, runner, parentVoted, secondsIntoSlot);

        Assert.That(runner.GetProposerHead(head.Root, proposalSlot: 3), Is.EqualTo(expectReorg ? parent.Root : head.Root));
    }

    /// <summary>
    /// Fulu's <c>get_proposer_head</c> (specs/fulu/fork-choice.md, EIP-7917) has no <c>is_shuffling_stable</c>, so a late,
    /// weak head in the last slot of an epoch is re-orged for a proposal in the first slot of the next, as one slot earlier.
    /// The slot-30 committee is empty, so the parent's vote there comes from the slot-31 committee.
    /// </summary>
    [TestCase(29ul, 29ul, TestName = "proposal_in_the_last_slot_of_the_epoch")]
    [TestCase(30ul, 31ul, TestName = "proposal_in_the_first_slot_of_the_next_epoch")]
    public void Late_weak_head_is_reorged_on_either_side_of_an_epoch_boundary(ulong parentSlot, ulong voteSlot)
    {
        UnsignedChain chain = UnsignedChain.Create();
        Assert.That(chain.Committee(voteSlot), Is.Not.Empty, "fixture bug: the parent's vote needs a committee member");
        ForkChoiceRunner runner = CreateRunner(chain);
        (UnsignedChain.ChainBlock parent, UnsignedChain.ChainBlock head) = LateHeadOnParent(chain, runner, parentVoted: true, secondsIntoSlot: 0, parentSlot, voteSlot);

        Assert.That(runner.GetProposerHead(head.Root, proposalSlot: parentSlot + 2), Is.EqualTo(parent.Root));
    }

    /// <summary>
    /// <c>is_parent_strong</c> needs the parent's score strictly above the threshold. A heavy validator outside the
    /// voting committee sets the justified total to 640 ETH plus <paramref name="totalOffsetGwei"/>, which puts the
    /// threshold exactly on the parent's single 32 ETH vote at offset 0, and 160 Gwei either side of it otherwise.
    /// </summary>
    [TestCase(-3200L, true, TestName = "score_above_the_threshold_is_strong")]
    [TestCase(0L, false, TestName = "score_equal_to_the_threshold_is_not_strong")]
    [TestCase(3200L, false, TestName = "score_below_the_threshold_is_not_strong")]
    public void Parent_is_strong_only_strictly_above_the_threshold(long totalOffsetGwei, bool expectReorg)
    {
        const ulong Gwei = 1_000_000_000;
        UnsignedChain chain = UnsignedChain.Create();
        Validator[] registry = chain.Anchor.AnchorState.Validators!;
        Assert.That(registry, Has.Length.EqualTo(16).And.All.Property(nameof(Validator.EffectiveBalance)).EqualTo(32 * Gwei), "fixture bug: the registry must be 16 validators of 32 ETH");
        Assert.That(chain.Committee(1), Has.Length.EqualTo(1), "fixture bug: the parent's vote must be a single validator");
        ulong ballastBalance = (ulong)((long)(160 * Gwei) + totalOffsetGwei);
        ForkChoiceRunner runner = CreateRunner(chain, new AnchorWithBallast(chain, ballastBalance));
        (UnsignedChain.ChainBlock parent, UnsignedChain.ChainBlock head) = LateHeadOnParent(chain, runner, parentVoted: true, secondsIntoSlot: 0);

        Assert.That(runner.GetProposerHead(head.Root, proposalSlot: 3), Is.EqualTo(expectReorg ? parent.Root : head.Root));
    }

    /// <summary>
    /// A late, vote-less head at <paramref name="parentSlot"/> + 1 on a timely parent, with the clock <paramref name="secondsIntoSlot"/>
    /// into the slot after the head. The parent's vote is cast by the committee of <paramref name="voteSlot"/>, the parent's slot by default.
    /// </summary>
    private static (UnsignedChain.ChainBlock Parent, UnsignedChain.ChainBlock Head) LateHeadOnParent(UnsignedChain chain, ForkChoiceRunner runner, bool parentVoted, ulong secondsIntoSlot, ulong parentSlot = 1, ulong? voteSlot = null)
    {
        ulong headSlot = parentSlot + 1;
        UnsignedChain.ChainBlock parent = chain.Extend(chain.AnchorRoot, slot: parentSlot, payloadHashByte: 0xa1);
        UnsignedChain.ChainBlock head = chain.Extend(parent.Root, slot: headSlot, payloadHashByte: 0xa2);
        TickTo(runner, slot: parentSlot);
        Import(runner, parent);
        // Past the attestation deadline of the head's slot, so the head is late and gets no boost.
        TickTo(runner, slot: headSlot, secondsIntoSlot: 5);
        Import(runner, head);
        TickTo(runner, slot: headSlot + 1, secondsIntoSlot);
        if (parentVoted) runner.OnAttestation(chain.Vote(voteSlot ?? parentSlot, parent.Root), verifySignature: false);
        Assert.That(runner.GetHead(), Is.EqualTo(head.Root), "fixture bug: the late block must be the head");
        return (parent, head);
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
