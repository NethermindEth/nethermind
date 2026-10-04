// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
#if GLOAS
using AccessorState = Nethermind.BeaconChain.Types.BeaconStateGloas;
#else
using AccessorState = Nethermind.BeaconChain.Types.BeaconStateFulu;
#endif

namespace Nethermind.BeaconChain.StateTransition;

#if GLOAS
public static partial class GloasStateAccessors
#else
public static partial class BeaconStateMutators
#endif
{
    public static partial void IncreaseBalance(this AccessorState state, int index, ulong delta) =>
        state.Balances![index] += delta;

    /// <summary>Decreases a validator's balance by <paramref name="delta"/>, saturating at zero.</summary>
    public static partial void DecreaseBalance(this AccessorState state, int index, ulong delta)
    {
        ref ulong balance = ref state.Balances![index];
        balance -= Math.Min(balance, delta);
    }

    /// <summary>
    /// Computes the earliest epoch with enough EIP-7251 exit churn for <paramref name="exitBalance"/>
    /// and consumes that churn (spec <c>compute_exit_epoch_and_update_churn</c>).
    /// </summary>
    public static partial ulong ComputeExitEpochAndUpdateChurn(this AccessorState state, ulong exitBalance, EpochCache cache)
    {
        ulong earliestExitEpoch = Math.Max(state.EarliestExitEpoch, BeaconStateAccessors.ComputeActivationExitEpoch(state.GetCurrentEpoch()));
#if GLOAS
        ulong perEpochChurn = state.GetExitChurnLimit(cache);
#else
        ulong perEpochChurn = state.GetActivationExitChurnLimit(cache);
#endif
        // New epoch for exits.
        ulong exitBalanceToConsume = state.EarliestExitEpoch < earliestExitEpoch ? perEpochChurn : state.ExitBalanceToConsume;

        // Exit doesn't fit in the current earliest epoch.
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

    /// <summary>
    /// Computes the earliest epoch with enough EIP-7251 consolidation churn for
    /// <paramref name="consolidationBalance"/> and consumes that churn
    /// (spec <c>compute_consolidation_epoch_and_update_churn</c>).
    /// </summary>
    public static partial ulong ComputeConsolidationEpochAndUpdateChurn(this AccessorState state, ulong consolidationBalance, EpochCache cache)
    {
        ulong earliestConsolidationEpoch = Math.Max(state.EarliestConsolidationEpoch, BeaconStateAccessors.ComputeActivationExitEpoch(state.GetCurrentEpoch()));
        ulong perEpochChurn = state.GetConsolidationChurnLimit(cache);
        // New epoch for consolidations.
        ulong balanceToConsume = state.EarliestConsolidationEpoch < earliestConsolidationEpoch ? perEpochChurn : state.ConsolidationBalanceToConsume;

        // Consolidation doesn't fit in the current earliest epoch.
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
    /// Initiates the exit of validator <paramref name="index"/> through the EIP-7251
    /// balance-weighted exit queue. No-op if an exit was already initiated.
    /// </summary>
    /// <exception cref="BeaconStateException">The withdrawable epoch overflows uint64.</exception>
    public static partial void InitiateValidatorExit(this AccessorState state, int index, EpochCache cache)
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

    /// <summary>
    /// Epoch addition that rejects uint64 overflow like the pyspec's <c>Epoch(...)</c> constructor
    /// (the <c>invalid_large_withdrawable_epoch</c> spec test relies on this).
    /// </summary>
#if GLOAS
    internal static partial ulong CheckedEpochSum(ulong epoch, ulong delta) =>
#else
    private static partial ulong CheckedEpochSum(ulong epoch, ulong delta) =>
#endif
        epoch <= Presets.FarFutureEpoch - delta
            ? epoch + delta
            : throw new BeaconStateException($"Epoch {epoch} + {delta} overflows uint64");

}
