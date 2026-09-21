// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Read-only accessors and mutators over <see cref="BeaconStateGloas"/> needed by
/// <see cref="GloasBlockProcessing"/>: the subset of <see cref="BeaconStateAccessors"/> and
/// <see cref="BeaconStateMutators"/> whose field reads (slot, randao mixes, proposer lookahead,
/// balances, fork) are identical between <see cref="BeaconStateFulu"/> and
/// <see cref="BeaconStateGloas"/> (see the field-by-field comparison in
/// <see cref="GloasForkTransition.UpgradeToGloas"/>). Duplicated rather than shared through a
/// common base or interface for the same reason <see cref="ForkedBeaconState"/> wraps two concrete
/// types instead of introducing one: the two state classes are deliberately not related by
/// inheritance (see the remarks on <see cref="BeaconStateGloas"/>).
/// </summary>
public static class GloasStateAccessors
{
    public static ulong GetCurrentEpoch(this BeaconStateGloas state) => BeaconStateAccessors.ComputeEpochAtSlot(state.Slot);

    public static ulong GetPreviousEpoch(this BeaconStateGloas state)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        return currentEpoch == Presets.GenesisEpoch ? Presets.GenesisEpoch : currentEpoch - 1;
    }

    /// <summary>Spec <c>get_block_root</c>: the block root at the start slot of a recent <paramref name="epoch"/>.</summary>
    public static Hash256 GetBlockRoot(this BeaconStateGloas state, ulong epoch) =>
        state.GetBlockRootAtSlot(BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch));

    /// <summary>Spec <c>get_seed</c> (unmodified in Gloas): the shuffling seed for <paramref name="epoch"/> and the given domain type.</summary>
    /// <remarks>
    /// Same shape as the Fulu <see cref="BeaconStateAccessors.GetSeed"/>, and for the same reason:
    /// the spec's <c>epoch + EPOCHS_PER_HISTORICAL_VECTOR - MIN_SEED_LOOKAHEAD - 1</c> lands on the
    /// same vector slot as the plain <c>epoch - MIN_SEED_LOOKAHEAD - 1</c> (the vector length divides
    /// 2^64), but inflating the epoch would push every call outside the window
    /// <see cref="GetRandaoMix"/> enforces. Only the previous, current and next epoch (and their
    /// lookahead) have a mix the window still covers.
    /// </remarks>
    /// <exception cref="BeaconStateException">The mix epoch the seed derives from is outside the historical-vector window.</exception>
    public static Hash256 GetSeed(this BeaconStateGloas state, ulong epoch, ReadOnlySpan<byte> domainType)
    {
        Hash256 mix = state.GetRandaoMix(epoch - Presets.MinSeedLookahead - 1);
        Span<byte> preimage = stackalloc byte[4 + 8 + 32];
        domainType.CopyTo(preimage);
        BinaryPrimitives.WriteUInt64LittleEndian(preimage[4..], epoch);
        mix.Bytes.CopyTo(preimage[12..]);
        return new Hash256(SHA256.HashData(preimage));
    }

    /// <summary>Spec <c>get_randao_mix</c>: returns the randao mix recorded for <paramref name="epoch"/>.</summary>
    /// <remarks>See the Fulu <see cref="BeaconStateAccessors.GetRandaoMix"/> remarks for the window this enforces.</remarks>
    /// <exception cref="BeaconStateException">The epoch is outside the historical-vector window.</exception>
    public static Hash256 GetRandaoMix(this BeaconStateGloas state, ulong epoch)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        ulong age = currentEpoch - epoch; // unsigned wraparound: also rejects epoch > currentEpoch
        if (age >= Presets.EpochsPerHistoricalVector)
            throw new BeaconStateException(
                $"Randao mix for epoch {epoch} is not available: state is at epoch {currentEpoch}, " +
                $"window covers the {Presets.EpochsPerHistoricalVector} epochs up to and including it");
        return state.RandaoMixes![(int)(epoch % Presets.EpochsPerHistoricalVector)];
    }

    /// <summary>Spec <c>get_block_root_at_slot</c>.</summary>
    /// <exception cref="BeaconStateException">The slot is not within the last <c>SLOTS_PER_HISTORICAL_ROOT</c> slots.</exception>
    public static Hash256 GetBlockRootAtSlot(this BeaconStateGloas state, ulong slot)
    {
        if (!(slot < state.Slot && state.Slot <= slot + Presets.SlotsPerHistoricalRoot))
            throw new BeaconStateException($"Block root for slot {slot} is not available at state slot {state.Slot}");
        return state.BlockRoots![(int)(slot % Presets.SlotsPerHistoricalRoot)];
    }

    /// <summary>
    /// Returns the root of the last block that could influence the attester shuffling for
    /// <paramref name="epoch"/>. Used as a fork-safe cache key; see
    /// <see cref="BeaconStateAccessors.GetShufflingDecisionRoot"/> for the Fulu twin.
    /// </summary>
    public static Hash256 GetShufflingDecisionRoot(this BeaconStateGloas state, ulong epoch)
    {
        ulong decisionSlot = epoch >= Presets.MinSeedLookahead
            ? BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch - Presets.MinSeedLookahead)
            : 0;
        if (decisionSlot > 0)
            decisionSlot--;
        return state.Slot == 0 ? Hash256.Zero : state.GetBlockRootAtSlot(decisionSlot);
    }

    /// <summary>Returns the signing domain for the given domain type at <paramref name="epoch"/> (current epoch when null).</summary>
    public static Hash256 GetDomain(this BeaconStateGloas state, ReadOnlySpan<byte> domainType, ulong? epoch = null)
    {
        ulong atEpoch = epoch ?? state.GetCurrentEpoch();
        Fork fork = state.Fork!;
        byte[] forkVersion = atEpoch < fork.Epoch ? fork.PreviousVersion! : fork.CurrentVersion!;
        return Domains.ComputeDomain(domainType, forkVersion, state.GenesisValidatorsRoot!);
    }

    /// <summary>Returns the proposer index at the state's current slot (EIP-7917 lookahead, unmodified in Gloas).</summary>
    public static ulong GetBeaconProposerIndex(this BeaconStateGloas state) =>
        state.ProposerLookahead![(int)(state.Slot % Presets.SlotsPerEpoch)];

    public static ulong ComputeTimeAtSlot(this BeaconStateGloas state, ulong slot) =>
        state.GenesisTime + (slot - Presets.GenesisSlot) * Presets.SecondsPerSlot;

    public static void IncreaseBalance(this BeaconStateGloas state, int index, ulong delta) =>
        state.Balances![index] += delta;

    /// <summary>Decreases a validator's balance by <paramref name="delta"/>, saturating at zero.</summary>
    public static void DecreaseBalance(this BeaconStateGloas state, int index, ulong delta)
    {
        ref ulong balance = ref state.Balances![index];
        balance -= Math.Min(balance, delta);
    }

    /// <summary>Returns the total amount queued in <c>pending_partial_withdrawals</c> for a validator (Electra semantics, unmodified in Gloas).</summary>
    public static ulong GetPendingBalanceToWithdraw(this BeaconStateGloas state, int validatorIndex)
    {
        ulong pendingBalance = 0;
        foreach (PendingPartialWithdrawal withdrawal in state.PendingPartialWithdrawals!)
        {
            if (withdrawal.ValidatorIndex == (ulong)validatorIndex)
                pendingBalance += withdrawal.Amount;
        }
        return pendingBalance;
    }

    /// <summary>
    /// Spec <c>is_active_builder</c>: the builder's registry placement is finalized and it has not
    /// initiated an exit.
    /// </summary>
    public static bool IsActiveBuilder(this BeaconStateGloas state, ulong builderIndex)
    {
        Builder builder = state.Builders![(int)builderIndex];
        return builder.DepositEpoch < state.FinalizedCheckpoint!.Epoch && builder.WithdrawableEpoch == Presets.FarFutureEpoch;
    }

    /// <summary>Spec <c>get_pending_balance_to_withdraw_for_builder</c>: sums both the withdrawal queue and any not-yet-settled pending payment naming this builder.</summary>
    public static ulong GetPendingBalanceToWithdrawForBuilder(this BeaconStateGloas state, ulong builderIndex)
    {
        ulong balance = 0;
        foreach (BuilderPendingWithdrawal withdrawal in state.BuilderPendingWithdrawals ?? [])
        {
            if (withdrawal.BuilderIndex == builderIndex)
                balance += withdrawal.Amount;
        }
        foreach (BuilderPendingPayment payment in state.BuilderPendingPayments ?? [])
        {
            if (payment.Withdrawal!.BuilderIndex == builderIndex)
                balance += payment.Withdrawal.Amount;
        }
        return balance;
    }

    /// <summary>Spec <c>can_builder_cover_bid</c>: the builder's balance, net of everything already queued to leave it, still covers <paramref name="bidAmount"/> above the minimum deposit floor.</summary>
    public static bool CanBuilderCoverBid(this BeaconStateGloas state, ulong builderIndex, ulong bidAmount)
    {
        ulong builderBalance = state.Builders![(int)builderIndex].Balance;
        ulong pendingWithdrawalsAmount = state.GetPendingBalanceToWithdrawForBuilder(builderIndex);
        ulong minBalance = Presets.MinDepositAmount + pendingWithdrawalsAmount;
        if (builderBalance < minBalance)
            return false;
        return builderBalance - minBalance >= bidAmount;
    }

    /// <summary>Returns the indices of validators active in <paramref name="epoch"/>, in ascending order.</summary>
    public static int[] GetActiveValidatorIndices(this BeaconStateGloas state, ulong epoch)
    {
        Validator[] validators = state.Validators!;
        List<int> active = new(validators.Length);
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].IsActiveValidator(epoch))
                active.Add(i);
        }
        return active.ToArray();
    }

    /// <summary>Returns <c>max(EFFECTIVE_BALANCE_INCREMENT, sum of effective balances)</c> of the given validators.</summary>
    public static ulong GetTotalBalance(this BeaconStateGloas state, IEnumerable<int> indices)
    {
        ulong total = 0;
        foreach (int index in indices)
        {
            total += state.Validators![index].EffectiveBalance;
        }
        return Math.Max(Presets.EffectiveBalanceIncrement, total);
    }

    /// <summary>Returns the total effective balance of active validators, memoized per epoch in <paramref name="cache"/>.</summary>
    public static ulong GetTotalActiveBalance(this BeaconStateGloas state, EpochCache cache) =>
        cache.GetTotalActiveBalance(state);

    /// <summary>Spec <c>get_base_reward_per_increment</c>.</summary>
    public static ulong GetBaseRewardPerIncrement(this BeaconStateGloas state, EpochCache cache) =>
        Presets.EffectiveBalanceIncrement * Presets.BaseRewardFactor / BeaconStateAccessors.IntegerSquareRoot(state.GetTotalActiveBalance(cache));

    /// <summary>
    /// Spec <c>get_activation_churn_limit</c> (EIP-8061, new in Gloas): the capped per-epoch churn
    /// pending deposits consume. Exits no longer share this budget - see <see cref="GetExitChurnLimit"/>.
    /// </summary>
    public static ulong GetActivationChurnLimit(this BeaconStateGloas state, EpochCache cache) =>
        Math.Min(Presets.MaxPerEpochActivationChurnLimitGloas, state.GetExitChurnLimit(cache));

    /// <summary>Spec <c>get_exit_churn_limit</c> (EIP-8061, new in Gloas): the uncapped per-epoch exit churn.</summary>
    public static ulong GetExitChurnLimit(this BeaconStateGloas state, EpochCache cache)
    {
        ulong churn = Math.Max(Presets.MinPerEpochChurnLimitElectra, state.GetTotalActiveBalance(cache) / Presets.ChurnLimitQuotientGloas);
        return churn - churn % Presets.EffectiveBalanceIncrement;
    }

    /// <summary>
    /// Spec <c>get_consolidation_churn_limit</c> (modified in Gloas): derived directly from the total
    /// active balance instead of as the remainder of the Electra balance churn.
    /// </summary>
    public static ulong GetConsolidationChurnLimit(this BeaconStateGloas state, EpochCache cache)
    {
        ulong churn = state.GetTotalActiveBalance(cache) / Presets.ConsolidationChurnLimitQuotient;
        return churn - churn % Presets.EffectiveBalanceIncrement;
    }

    /// <summary>Spec <c>get_builder_payment_quorum_threshold</c>: the PTC weight a pending builder payment needs to be honored at the epoch boundary.</summary>
    public static ulong GetBuilderPaymentQuorumThreshold(this BeaconStateGloas state, EpochCache cache)
    {
        ulong perSlotBalance = state.GetTotalActiveBalance(cache) / Presets.SlotsPerEpoch;
        return perSlotBalance * Presets.BuilderPaymentThresholdNumerator / Presets.BuilderPaymentThresholdDenominator;
    }

    /// <summary>Spec <c>compute_exit_epoch_and_update_churn</c> (modified in Gloas: exits draw on <see cref="GetExitChurnLimit"/>).</summary>
    public static ulong ComputeExitEpochAndUpdateChurn(this BeaconStateGloas state, ulong exitBalance, EpochCache cache)
    {
        ulong earliestExitEpoch = Math.Max(state.EarliestExitEpoch, BeaconStateAccessors.ComputeActivationExitEpoch(state.GetCurrentEpoch()));
        ulong perEpochChurn = state.GetExitChurnLimit(cache);
        ulong exitBalanceToConsume = state.EarliestExitEpoch < earliestExitEpoch ? perEpochChurn : state.ExitBalanceToConsume;

        if (exitBalance > exitBalanceToConsume)
        {
            ulong balanceToProcess = exitBalance - exitBalanceToConsume;
            ulong additionalEpochs = (balanceToProcess - 1) / perEpochChurn + 1;
            earliestExitEpoch += additionalEpochs;
            exitBalanceToConsume += additionalEpochs * perEpochChurn;
        }

        state.ExitBalanceToConsume = exitBalanceToConsume - exitBalance;
        state.EarliestExitEpoch = earliestExitEpoch;
        return earliestExitEpoch;
    }

    /// <summary>Spec <c>compute_consolidation_epoch_and_update_churn</c>.</summary>
    public static ulong ComputeConsolidationEpochAndUpdateChurn(this BeaconStateGloas state, ulong consolidationBalance, EpochCache cache)
    {
        ulong earliestConsolidationEpoch = Math.Max(state.EarliestConsolidationEpoch, BeaconStateAccessors.ComputeActivationExitEpoch(state.GetCurrentEpoch()));
        ulong perEpochChurn = state.GetConsolidationChurnLimit(cache);
        ulong balanceToConsume = state.EarliestConsolidationEpoch < earliestConsolidationEpoch ? perEpochChurn : state.ConsolidationBalanceToConsume;

        if (consolidationBalance > balanceToConsume)
        {
            ulong balanceToProcess = consolidationBalance - balanceToConsume;
            ulong additionalEpochs = (balanceToProcess - 1) / perEpochChurn + 1;
            earliestConsolidationEpoch += additionalEpochs;
            balanceToConsume += additionalEpochs * perEpochChurn;
        }

        state.ConsolidationBalanceToConsume = balanceToConsume - consolidationBalance;
        state.EarliestConsolidationEpoch = earliestConsolidationEpoch;
        return earliestConsolidationEpoch;
    }

    /// <summary>
    /// Initiates the exit of validator <paramref name="index"/> through the EIP-7251 balance-weighted
    /// exit queue. No-op if an exit was already initiated.
    /// </summary>
    public static void InitiateValidatorExit(this BeaconStateGloas state, int index, EpochCache cache)
    {
        Validator validator = state.Validators![index];
        if (validator.ExitEpoch != Presets.FarFutureEpoch)
            return;

        ulong exitQueueEpoch = state.ComputeExitEpochAndUpdateChurn(validator.EffectiveBalance, cache);

        Validator updated = validator.Clone();
        updated.ExitEpoch = exitQueueEpoch;
        updated.WithdrawableEpoch = CheckedEpochSum(exitQueueEpoch, Presets.MinValidatorWithdrawabilityDelay);
        state.Validators[index] = updated;
    }

    /// <summary>Spec <c>add_validator_to_registry</c> (Electra, unmodified in Gloas): appends a deposit-derived validator and its per-validator list entries.</summary>
    public static void AddValidatorToRegistry(this BeaconStateGloas state, BlsPublicKey pubkey, Hash256 withdrawalCredentials, ulong amount)
    {
        Validator validator = new()
        {
            Pubkey = pubkey,
            WithdrawalCredentials = withdrawalCredentials,
            EffectiveBalance = 0,
            Slashed = false,
            ActivationEligibilityEpoch = Presets.FarFutureEpoch,
            ActivationEpoch = Presets.FarFutureEpoch,
            ExitEpoch = Presets.FarFutureEpoch,
            WithdrawableEpoch = Presets.FarFutureEpoch,
        };
        validator.EffectiveBalance = Math.Min(amount - amount % Presets.EffectiveBalanceIncrement, validator.GetMaxEffectiveBalance());

        state.Validators = [.. state.Validators!, validator];
        state.Balances = [.. state.Balances!, amount];
        state.PreviousEpochParticipation = [.. state.PreviousEpochParticipation!, 0];
        state.CurrentEpochParticipation = [.. state.CurrentEpochParticipation!, 0];
        state.InactivityScores = [.. state.InactivityScores!, 0UL];
    }

    /// <summary>Spec <c>get_base_reward</c> (Altair, unmodified in Gloas).</summary>
    public static ulong GetBaseReward(this BeaconStateGloas state, int index, EpochCache cache) =>
        state.Validators![index].EffectiveBalance / Presets.EffectiveBalanceIncrement * state.GetBaseRewardPerIncrement(cache);

    /// <summary>
    /// Returns the validator indices attesting in a Gloas (EIP-7549 shape) aggregate, in ascending
    /// order, validating the committee/aggregation bit structure as in <c>process_attestation</c>.
    /// </summary>
    /// <param name="committees">Committee cache built for the epoch of <c>attestation.Data.Slot</c>.</param>
    /// <exception cref="BeaconStateException">The attestation's bitfields are inconsistent with the committees.</exception>
    public static ulong[] GetAttestingIndices(this BeaconStateGloas state, AttestationGloas attestation, CommitteeCache committees)
    {
        ulong slot = attestation.Data!.Slot;
        BitArray committeeBits = attestation.CommitteeBits!;
        BitArray aggregationBits = attestation.AggregationBits!;

        List<ulong> attestingIndices = [];
        int committeeOffset = 0;
        for (int committeeIndex = 0; committeeIndex < committeeBits.Length; committeeIndex++)
        {
            if (!committeeBits[committeeIndex])
                continue;
            if (committeeIndex >= committees.CommitteesPerSlot)
                throw new BeaconStateException($"Committee index {committeeIndex} out of range ({committees.CommitteesPerSlot} committees per slot)");

            ReadOnlySpan<int> committee = committees.GetBeaconCommittee(slot, committeeIndex);
            int attestersInCommittee = 0;
            for (int i = 0; i < committee.Length; i++)
            {
                int bitIndex = committeeOffset + i;
                if (bitIndex < aggregationBits.Length && aggregationBits[bitIndex])
                {
                    attestingIndices.Add((ulong)committee[i]);
                    attestersInCommittee++;
                }
            }
            if (attestersInCommittee == 0)
                throw new BeaconStateException($"Committee {committeeIndex} has no attesters set");
            committeeOffset += committee.Length;
        }

        if (committeeOffset != aggregationBits.Length)
            throw new BeaconStateException($"Aggregation bits length {aggregationBits.Length} does not match participant count {committeeOffset}");

        attestingIndices.Sort();
        return attestingIndices.ToArray();
    }

    /// <summary>Converts a Gloas attestation to its indexed, signature-verifiable form.</summary>
    /// <param name="committees">Committee cache built for the epoch of <c>attestation.Data.Slot</c>.</param>
    public static IndexedAttestationGloas GetIndexedAttestation(this BeaconStateGloas state, AttestationGloas attestation, CommitteeCache committees) =>
        new()
        {
            AttestingIndices = state.GetAttestingIndices(attestation, committees),
            Data = attestation.Data,
            Signature = attestation.Signature,
        };

    /// <summary>
    /// Spec <c>is_attestation_same_slot</c> (new in Gloas): whether the attestation votes for the
    /// block proposed at its own slot, i.e. that slot's root is the vote and differs from the
    /// previous slot's (a skipped slot repeats the previous root).
    /// </summary>
    public static bool IsAttestationSameSlot(this BeaconStateGloas state, AttestationData data)
    {
        if (data.Slot == 0)
            return true;

        Hash256 blockRoot = data.BeaconBlockRoot!;
        Hash256 slotBlockRoot = state.GetBlockRootAtSlot(data.Slot);
        Hash256 previousBlockRoot = state.GetBlockRootAtSlot(data.Slot - 1);
        return blockRoot == slotBlockRoot && blockRoot != previousBlockRoot;
    }

    /// <summary>
    /// Spec <c>get_ptc</c> (new in Gloas): the payload timeliness committee for <paramref name="slot"/>,
    /// read from the window <see cref="GloasEpochProcessing.ProcessPtcWindow"/> maintains (the
    /// previous epoch in the first <c>SLOTS_PER_EPOCH</c> entries, then the current epoch and the
    /// <c>MIN_SEED_LOOKAHEAD</c> epochs after it).
    /// </summary>
    /// <exception cref="BeaconStateException">The slot's epoch is outside the window.</exception>
    public static PayloadTimelinessCommittee GetPtc(this BeaconStateGloas state, ulong slot)
    {
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(slot);
        ulong stateEpoch = state.GetCurrentEpoch();
        ulong slotInEpoch = slot % Presets.SlotsPerEpoch;
        if (epoch < stateEpoch)
        {
            if (epoch + 1 != stateEpoch)
                throw new BeaconStateException($"PTC for slot {slot} is not available: epoch {epoch} is before the previous epoch at state epoch {stateEpoch}");
            return state.PtcWindow![(int)slotInEpoch];
        }

        if (epoch > stateEpoch + Presets.MinSeedLookahead)
            throw new BeaconStateException($"PTC for slot {slot} is not available: epoch {epoch} is beyond the lookahead at state epoch {stateEpoch}");
        ulong offset = (epoch - stateEpoch + 1) * Presets.SlotsPerEpoch;
        return state.PtcWindow![(int)(offset + slotInEpoch)];
    }

    /// <summary>Spec <c>get_indexed_payload_attestation</c> (new in Gloas): resolves the set PTC bits to validator indices, sorted ascending.</summary>
    /// <exception cref="BeaconStateException">The bitvector's length is not the PTC size, or the slot's PTC is not available.</exception>
    public static IndexedPayloadAttestation GetIndexedPayloadAttestation(this BeaconStateGloas state, PayloadAttestation attestation)
    {
        // The pre-fork history is the spec's default committee (validator 0 at every position), which
        // the upgrade leaves unpopulated; a vote for one of those slots is invalid, not a crash.
        ulong[] ptc = state.GetPtc(attestation.Data!.Slot).Indices ?? new ulong[Presets.PtcSize];
        BitArray bits = attestation.AggregationBits!;
        if (bits.Length != ptc.Length)
            throw new BeaconStateException($"Payload attestation has {bits.Length} aggregation bits, expected {ptc.Length}");

        List<ulong> attestingIndices = [];
        for (int i = 0; i < ptc.Length; i++)
        {
            if (bits[i])
                attestingIndices.Add(ptc[i]);
        }
        attestingIndices.Sort();

        return new IndexedPayloadAttestation
        {
            AttestingIndices = attestingIndices.ToArray(),
            Data = attestation.Data,
            Signature = attestation.Signature,
        };
    }

    /// <summary>Epoch addition that rejects uint64 overflow like the pyspec's <c>Epoch(...)</c> constructor.</summary>
    internal static ulong CheckedEpochSum(ulong epoch, ulong delta) =>
        epoch <= Presets.FarFutureEpoch - delta
            ? epoch + delta
            : throw new BeaconStateException($"Epoch {epoch} + {delta} overflows uint64");
}
