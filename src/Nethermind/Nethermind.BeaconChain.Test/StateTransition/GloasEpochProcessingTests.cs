// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// Gloas epoch processing (<see cref="GloasEpochProcessing"/>) pinned two ways: differentially
/// against the already-tested Fulu pipeline for every step Gloas left unchanged, and directly for
/// the three steps it changed - the builder payment window rotation (and the three settlement
/// branches in block processing that depend on it), the PTC window rotation, and the payload
/// availability reset in <c>process_slot</c>.
/// </summary>
public class GloasEpochProcessingTests
{
    private static readonly ulong SlotsPerEpoch = Presets.SlotsPerEpoch;

    // ---- The defect this replaces: a same-slot-in-epoch bid one epoch later used to throw ----

    /// <summary>
    /// Before epoch processing existed, the payment window never rotated, so a bid at the same
    /// slot-in-epoch as a still-unsettled payment from the previous epoch had to throw by name
    /// rather than overwrite it. With the rotation in place the old payment has moved to the
    /// lower half and the new one lands in a cleared address; neither is lost.
    /// </summary>
    [Test]
    public void A_bid_at_the_same_slot_in_epoch_as_an_unsettled_payment_from_the_previous_epoch_lands_beside_it_not_on_top_of_it()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        EpochCache cache = new();
        const ulong firstBidValue = 4 * Gwei;
        const ulong secondBidValue = 6 * Gwei;

        // Slot 32: a builder bid commits a real payment at slot-in-epoch 0 (upper-half index 32).
        ApplyBlock(state, MinimalBlock(state, ValidBuilderBid(state, builderSk, builderIndex: 0, value: firstBidValue)), cache);
        // Slot 33: a self-build declaring the slot-32 payload undelivered, so nothing settles it.
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, parentBlockHash: state.LatestBlockHash!, blockHash: Hash(0x9A))), cache);
        Assert.That(state.BuilderPendingPayments![32].Withdrawal!.Amount, Is.EqualTo(firstBidValue), "fixture bug: the first payment must still be unsettled");

        // Cross the epoch boundary: the rotation moves the unsettled payment to the lower half.
        GloasSlotProcessing.ProcessSlots(state, 2 * SlotsPerEpoch, cache);
        Assert.That(state.BuilderPendingPayments[0].Withdrawal!.Amount, Is.EqualTo(firstBidValue), "the rotation must carry the unsettled payment into the previous-epoch half");
        Assert.That(state.BuilderPendingPayments[32].Withdrawal!.Amount, Is.EqualTo(0ul), "the rotation must clear the current-epoch half");

        // Slot 64, slot-in-epoch 0 again: a second real payment at the same address as the first.
        SignedExecutionPayloadBid secondBid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: secondBidValue, blockHashFill: 0x8B);
        Assert.DoesNotThrow(() => ApplyBlock(state, MinimalBlock(state, secondBid), cache));

        Assert.Multiple(() =>
        {
            Assert.That(state.BuilderPendingPayments[32].Withdrawal!.Amount, Is.EqualTo(secondBidValue));
            Assert.That(state.BuilderPendingPayments[0].Withdrawal!.Amount, Is.EqualTo(firstBidValue), "the second payment must not destroy the first");
        });
    }

    // ---- The three settlement branches of apply_parent_execution_payload ----

    [Test]
    public void A_payload_committed_in_the_previous_epoch_is_settled_through_the_rotated_lower_half_and_paid()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out ulong builderStartingBalance);
        EpochCache cache = new();
        const ulong bidValue = 4 * Gwei;

        // Slot 62 (slot-in-epoch 30): the bid commits a payment at upper-half index 62.
        GloasSlotProcessing.ProcessSlots(state, 62, cache);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: bidValue);
        ApplyBlock(state, MinimalBlock(state, bid), cache);
        Assert.That(state.BuilderPendingPayments![62].Withdrawal!.Amount, Is.EqualTo(bidValue));

        // Slot 64: the child block declares the slot-62 payload delivered. Its parent's epoch is now
        // the previous epoch, so settlement reads the lower-half address the rotation moved it to.
        GloasSlotProcessing.ProcessSlots(state, 64, cache);
        Assert.That(state.BuilderPendingPayments[30].Withdrawal!.Amount, Is.EqualTo(bidValue), "fixture bug: the rotation must have moved the payment to index 30");
        ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, parentBlockHash: bid.Message!.BlockHash!, blockHash: Hash(0x9B))), cache);

        Assert.Multiple(() =>
        {
            Assert.That(state.LatestBlockHash, Is.EqualTo(bid.Message.BlockHash));
            Assert.That(state.BuilderPendingPayments[30].Withdrawal!.Amount, Is.EqualTo(0ul), "settling must clear the previous-epoch address");
            Assert.That(state.BuilderPendingWithdrawals, Is.Empty, "the settled withdrawal must be paid out by the same block's withdrawals step");
            Assert.That(state.Builders![0].Balance, Is.EqualTo(builderStartingBalance - bidValue), "the builder must actually have been paid");
        });
    }

    [Test]
    public void A_payload_committed_more_than_one_epoch_ago_is_paid_from_the_bid_itself_after_its_window_entry_was_evicted()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out ulong builderStartingBalance);
        EpochCache cache = new();
        const ulong bidValue = 4 * Gwei;

        // Slot 32: the bid commits a payment; then two whole epochs pass with no block.
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: bidValue);
        ApplyBlock(state, MinimalBlock(state, bid), cache);
        GloasSlotProcessing.ProcessSlots(state, 3 * SlotsPerEpoch, cache);

        // Rotated once into the lower half, then evicted unpaid (weight 0 is below any quorum).
        Assert.That(state.GetPendingBalanceToWithdrawForBuilder(0), Is.EqualTo(0ul), "fixture bug: the window entry must have been evicted, not queued");

        // Slot 96: the child declares the slot-32 payload delivered; only the bid remembers the amount.
        ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, parentBlockHash: bid.Message!.BlockHash!, blockHash: Hash(0x9C))), cache);

        Assert.Multiple(() =>
        {
            Assert.That(state.LatestBlockHash, Is.EqualTo(bid.Message.BlockHash));
            Assert.That(state.Builders![0].Balance, Is.EqualTo(builderStartingBalance - bidValue), "an evicted payment is still owed and must be paid from the bid's own value");
            Assert.That(state.BuilderPendingWithdrawals, Is.Empty);
        });
    }

    // ---- process_builder_pending_payments in isolation ----

    [Test]
    public void ProcessBuilderPendingPayments_queues_only_quorum_reaching_previous_epoch_payments_and_rotates_the_window()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        EpochCache cache = new();
        ulong quorum = state.GetBuilderPaymentQuorumThreshold(cache);
        Assert.That(quorum, Is.GreaterThan(1ul), "fixture bug: a quorum this small cannot distinguish reaching it from missing it");

        BuilderPendingPayment reaching = Payment(weight: quorum, amount: 3 * Gwei, feeRecipientFill: 0xA1);
        BuilderPendingPayment justShort = Payment(weight: quorum - 1, amount: 5 * Gwei, feeRecipientFill: 0xA2);
        BuilderPendingPayment currentEpoch = Payment(weight: 0, amount: 7 * Gwei, feeRecipientFill: 0xA3);
        state.BuilderPendingPayments![3] = reaching;
        state.BuilderPendingPayments[5] = justShort;
        state.BuilderPendingPayments[40] = currentEpoch;

        GloasEpochProcessing.ProcessBuilderPendingPayments(state, cache);

        Assert.Multiple(() =>
        {
            Assert.That(state.BuilderPendingWithdrawals, Has.Length.EqualTo(1));
            Assert.That(state.BuilderPendingWithdrawals![0], Is.SameAs(reaching.Withdrawal), "exactly the payment that reached the quorum is queued");
            Assert.That(state.BuilderPendingPayments[8], Is.SameAs(currentEpoch), "the current-epoch half becomes the previous-epoch half");
            Assert.That(state.BuilderPendingPayments.Take(32).Where((p, i) => i != 8).Select(p => p.Withdrawal!.Amount), Has.All.EqualTo(0ul));
            Assert.That(state.BuilderPendingPayments.Skip(32).Select(p => p.Withdrawal!.Amount), Has.All.EqualTo(0ul), "the new current-epoch half is zeroed");
            Assert.That(state.BuilderPendingPayments, Has.Length.EqualTo((int)Presets.BuilderPendingPaymentsLength));
        });
    }

    // ---- process_ptc_window ----

    [Test]
    public void ProcessPtcWindow_shifts_one_epoch_and_fills_the_last_with_the_committees_the_fork_transition_would_compute()
    {
        BeaconStateFulu pre = CreateFuluStateAtBoundary(ValidatorCount);
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());
        PayloadTimelinessCommittee[] before = (PayloadTimelinessCommittee[])state.PtcWindow!.Clone();
        int slots = (int)SlotsPerEpoch;

        GloasEpochProcessing.ProcessPtcWindow(state);

        // The Fulu-typed initializer computes epochs (currentEpoch, currentEpoch + 1) from the same
        // validators and mixes; asking it for epoch 2 onward yields the epoch-3 committees the Gloas
        // rotation must have produced, through independently written seed/committee/selection code.
        PayloadTimelinessCommittee[] expected = GloasForkTransition.InitializePtcWindow(pre, state.GetCurrentEpoch() + 1);

        Assert.Multiple(() =>
        {
            Assert.That(state.PtcWindow.Take(2 * slots), Is.EqualTo(before.Skip(slots)).AsCollection, "the first two epochs shift down by one epoch");
            for (int i = 0; i < slots; i++)
            {
                ulong[] gloas = state.PtcWindow[2 * slots + i].Indices!;
                ulong[] fulu = expected[2 * slots + i].Indices!;
                Assert.That(gloas, Has.Length.EqualTo((int)Presets.PtcSize));
                Assert.That(gloas, Is.EqualTo(fulu).AsCollection, $"PTC for slot-in-epoch {i} of the newly filled epoch");
            }
            Assert.That(state.PtcWindow.Skip(2 * slots).Select(c => c.Indices!.Distinct().Count()), Has.Some.GreaterThan(1),
                "a real committee draws from many validators; an all-equal committee means the seed or candidate list is wrong");
        });
    }

    // ---- Everything Gloas left unchanged must agree with the Fulu pipeline ----

    [Test]
    public void Crossing_an_epoch_boundary_produces_the_same_validator_balance_and_lookahead_updates_as_the_fulu_pipeline()
    {
        BeaconStateFulu pre = CreateFuluStateAtBoundary(ValidatorCount);
        // Give the epoch something to do: uneven balances so effective balances move under hysteresis,
        // and participation so rewards, penalties and justification all have non-trivial inputs.
        for (int i = 0; i < pre.Validators!.Length; i++)
        {
            pre.Balances![i] = (ulong)(31 + i % 3) * Gwei + (ulong)i * 1_000_000;
            pre.PreviousEpochParticipation![i] = (byte)(i % 4 == 0 ? 0 : 0b111);
            pre.CurrentEpochParticipation![i] = (byte)(i % 5 == 0 ? 0 : 0b011);
        }
        BeaconStateFulu fulu = pre.Clone();
        BeaconStateGloas gloas = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());
        ulong target = 2 * SlotsPerEpoch;

        SlotProcessing.ProcessSlots(fulu, target, new EpochCache());
        GloasSlotProcessing.ProcessSlots(gloas, target, new EpochCache());

        Assert.Multiple(() =>
        {
            Assert.That(gloas.Slot, Is.EqualTo(target));
            Assert.That(gloas.Balances, Is.EqualTo(fulu.Balances).AsCollection);
            Assert.That(gloas.Validators!.Select(v => v.EffectiveBalance), Is.EqualTo(fulu.Validators!.Select(v => v.EffectiveBalance)).AsCollection);
            Assert.That(gloas.Validators!.Select(v => v.EffectiveBalance).Distinct().Count(), Is.GreaterThan(1), "fixture bug: effective balances must actually have been updated to differ");
            Assert.That(gloas.InactivityScores, Is.EqualTo(fulu.InactivityScores).AsCollection);
            Assert.That(gloas.ProposerLookahead, Is.EqualTo(fulu.ProposerLookahead).AsCollection);
            Assert.That(gloas.ProposerLookahead!.Distinct().Count(), Is.GreaterThan(1), "fixture bug: the lookahead must hold real proposers");
            Assert.That(gloas.RandaoMixes![2], Is.EqualTo(fulu.RandaoMixes![2]), "the next epoch's mix is seeded from the current one");
            Assert.That(gloas.PreviousEpochParticipation, Is.EqualTo(fulu.PreviousEpochParticipation).AsCollection);
            Assert.That(gloas.CurrentEpochParticipation, Is.EqualTo(fulu.CurrentEpochParticipation).AsCollection);
            Assert.That(gloas.CurrentJustifiedCheckpoint!.Epoch, Is.EqualTo(fulu.CurrentJustifiedCheckpoint!.Epoch));
            Assert.That(gloas.FinalizedCheckpoint!.Epoch, Is.EqualTo(fulu.FinalizedCheckpoint!.Epoch));
            Assert.That(gloas.JustificationBits!.Cast<bool>(), Is.EqualTo(fulu.JustificationBits!.Cast<bool>()).AsCollection);
            Assert.That(gloas.Slashings, Is.EqualTo(fulu.Slashings).AsCollection);
            Assert.That(gloas.Eth1DataVotes, Is.EqualTo(fulu.Eth1DataVotes).AsCollection);
        });
    }

    [Test]
    public void ProcessSlot_marks_the_next_slot_payload_unavailable_until_a_child_block_declares_it_delivered()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        BitArray availability = state.ExecutionPayloadAvailability!;
        Assert.That(availability[33], Is.True, "fixture bug: the upgrade marks all history available");

        GloasSlotProcessing.ProcessSlots(state, 34);

        Assert.Multiple(() =>
        {
            Assert.That(availability[33], Is.False, "process_slot at slot 32 unsets slot 33");
            Assert.That(availability[34], Is.False, "process_slot at slot 33 unsets slot 34");
            Assert.That(availability[32], Is.True, "the slot already processed under Fulu keeps its upgrade-time value");
        });
    }

    private static BuilderPendingPayment Payment(ulong weight, ulong amount, byte feeRecipientFill) => new()
    {
        Weight = weight,
        Withdrawal = new BuilderPendingWithdrawal { Amount = amount, BuilderIndex = 0, FeeRecipient = new Core.Address(BuilderWithdrawalCredentials(feeRecipientFill).Bytes[12..]) },
        ProposerIndex = 0,
    };
}
