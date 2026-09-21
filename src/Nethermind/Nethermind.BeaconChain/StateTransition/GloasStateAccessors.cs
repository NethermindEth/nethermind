// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
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

    /// <summary>Epoch addition that rejects uint64 overflow like the pyspec's <c>Epoch(...)</c> constructor.</summary>
    private static ulong CheckedEpochSum(ulong epoch, ulong delta) =>
        epoch <= Presets.FarFutureEpoch - delta
            ? epoch + delta
            : throw new BeaconStateException($"Epoch {epoch} + {delta} overflows uint64");
}
