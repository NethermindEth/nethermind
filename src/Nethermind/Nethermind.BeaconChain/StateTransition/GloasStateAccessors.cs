// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Read-only accessors and mutators over <see cref="BeaconStateGloas"/> needed by
/// <see cref="GloasBlockProcessing"/>. Common operations are generated as concrete partial methods;
/// Gloas-specific operations use its builder and payload fields.
/// </summary>
public static partial class GloasStateAccessors
{
    public static partial ulong GetCurrentEpoch(this BeaconStateGloas state);
    public static partial ulong GetPreviousEpoch(this BeaconStateGloas state);
    public static partial Hash256 GetBlockRoot(this BeaconStateGloas state, ulong epoch);
    public static partial Hash256 GetSeed(this BeaconStateGloas state, ulong epoch, ReadOnlySpan<byte> domainType);
    public static partial Hash256 GetRandaoMix(this BeaconStateGloas state, ulong epoch);
    public static partial Hash256 GetBlockRootAtSlot(this BeaconStateGloas state, ulong slot);
    public static partial Hash256 GetShufflingDecisionRoot(this BeaconStateGloas state, ulong epoch);
    public static partial Hash256 GetDomain(this BeaconStateGloas state, ReadOnlySpan<byte> domainType, ulong? epoch = null);
    public static partial ulong GetBeaconProposerIndex(this BeaconStateGloas state);

    public static ulong ComputeTimeAtSlot(this BeaconStateGloas state, ulong slot) =>
        state.GenesisTime + (slot - Presets.GenesisSlot) * Presets.SecondsPerSlot;

    public static partial void IncreaseBalance(this BeaconStateGloas state, int index, ulong delta);
    public static partial void DecreaseBalance(this BeaconStateGloas state, int index, ulong delta);
    public static partial ulong GetPendingBalanceToWithdraw(this BeaconStateGloas state, int validatorIndex);

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

    public static partial int[] GetActiveValidatorIndices(this BeaconStateGloas state, ulong epoch);
    public static partial ulong GetTotalBalance(this BeaconStateGloas state, IEnumerable<int> indices);
    public static partial ulong GetTotalActiveBalance(this BeaconStateGloas state, EpochCache cache);
    public static partial ulong GetBaseRewardPerIncrement(this BeaconStateGloas state, EpochCache cache);

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

    public static partial ulong ComputeExitEpochAndUpdateChurn(this BeaconStateGloas state, ulong exitBalance, EpochCache cache);
    public static partial ulong ComputeConsolidationEpochAndUpdateChurn(this BeaconStateGloas state, ulong consolidationBalance, EpochCache cache);
    public static partial void InitiateValidatorExit(this BeaconStateGloas state, int index, EpochCache cache);

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

    public static partial ulong[] GetAttestingIndices(this BeaconStateGloas state, AttestationGloas attestation, CommitteeCache committees);
    public static partial IndexedAttestationGloas GetIndexedAttestation(this BeaconStateGloas state, AttestationGloas attestation, CommitteeCache committees);

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
    /// <exception cref="BeaconStateException">The slot's epoch is before <c>GLOAS_FORK_EPOCH</c> or outside the window.</exception>
    public static PayloadTimelinessCommittee GetPtc(this BeaconStateGloas state, ulong slot, BeaconChainSpec spec)
    {
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(slot);
        // Spec get_ptc asserts epoch >= GLOAS_FORK_EPOCH: pre-fork window entries are placeholders, not committees.
        if (epoch < spec.GloasForkEpoch)
            throw new BeaconStateException($"PTC for slot {slot} is not available: epoch {epoch} is before GLOAS_FORK_EPOCH {spec.GloasForkEpoch}");
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
    public static IndexedPayloadAttestation GetIndexedPayloadAttestation(this BeaconStateGloas state, PayloadAttestation attestation, BeaconChainSpec spec)
    {
        ulong[] ptc = state.GetPtc(attestation.Data!.Slot, spec).Indices!;
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

    internal static partial ulong CheckedEpochSum(ulong epoch, ulong delta);
}
