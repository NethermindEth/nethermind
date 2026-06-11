// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Consensus-specs mutators over <see cref="BeaconStateFulu"/> (Electra/Fulu semantics).
/// </summary>
/// <remarks>
/// <see cref="Validator"/> instances are treated as immutable: mutators install a modified copy
/// (see <see cref="Clone"/>) instead of writing through a possibly shared instance.
/// </remarks>
public static class BeaconStateMutators
{
    /// <summary>Returns a field-by-field copy, the supported way to "mutate" a validator in the registry.</summary>
    public static Validator Clone(this Validator validator) => new()
    {
        Pubkey = validator.Pubkey,
        WithdrawalCredentials = validator.WithdrawalCredentials,
        EffectiveBalance = validator.EffectiveBalance,
        Slashed = validator.Slashed,
        ActivationEligibilityEpoch = validator.ActivationEligibilityEpoch,
        ActivationEpoch = validator.ActivationEpoch,
        ExitEpoch = validator.ExitEpoch,
        WithdrawableEpoch = validator.WithdrawableEpoch,
    };

    public static void IncreaseBalance(this BeaconStateFulu state, int index, ulong delta) =>
        state.Balances![index] += delta;

    /// <summary>Decreases a validator's balance by <paramref name="delta"/>, saturating at zero.</summary>
    public static void DecreaseBalance(this BeaconStateFulu state, int index, ulong delta)
    {
        ref ulong balance = ref state.Balances![index];
        balance -= Math.Min(balance, delta);
    }

    /// <summary>
    /// Computes the earliest epoch with enough EIP-7251 exit churn for <paramref name="exitBalance"/>
    /// and consumes that churn (spec <c>compute_exit_epoch_and_update_churn</c>).
    /// </summary>
    public static ulong ComputeExitEpochAndUpdateChurn(this BeaconStateFulu state, ulong exitBalance, EpochCache cache)
    {
        ulong earliestExitEpoch = Math.Max(state.EarliestExitEpoch, BeaconStateAccessors.ComputeActivationExitEpoch(state.GetCurrentEpoch()));
        ulong perEpochChurn = state.GetActivationExitChurnLimit(cache);
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
    public static ulong ComputeConsolidationEpochAndUpdateChurn(this BeaconStateFulu state, ulong consolidationBalance, EpochCache cache)
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
    public static void InitiateValidatorExit(this BeaconStateFulu state, int index, EpochCache cache)
    {
        Validator validator = state.Validators![index];
        if (validator.ExitEpoch != Presets.FarFutureEpoch)
            return;

        ulong exitQueueEpoch = state.ComputeExitEpochAndUpdateChurn(validator.EffectiveBalance, cache);

        Validator updated = validator.Clone();
        updated.ExitEpoch = exitQueueEpoch;
        updated.WithdrawableEpoch = exitQueueEpoch + Presets.MinValidatorWithdrawabilityDelay;
        state.Validators[index] = updated;
    }

    /// <summary>
    /// Slashes validator <paramref name="slashedIndex"/> with Electra penalties: initiates its exit,
    /// applies the slashing penalty, and credits proposer and whistleblower rewards.
    /// </summary>
    /// <param name="whistleblowerIndex">Reward recipient; the current proposer when null.</param>
    public static void SlashValidator(this BeaconStateFulu state, int slashedIndex, EpochCache cache, int? whistleblowerIndex = null)
    {
        ulong epoch = state.GetCurrentEpoch();
        state.InitiateValidatorExit(slashedIndex, cache);

        Validator validator = state.Validators![slashedIndex].Clone();
        validator.Slashed = true;
        validator.WithdrawableEpoch = Math.Max(validator.WithdrawableEpoch, epoch + Presets.EpochsPerSlashingsVector);
        state.Validators[slashedIndex] = validator;

        state.Slashings![(int)(epoch % Presets.EpochsPerSlashingsVector)] += validator.EffectiveBalance;
        state.DecreaseBalance(slashedIndex, validator.EffectiveBalance / Presets.MinSlashingPenaltyQuotientElectra);

        // Apply proposer and whistleblower rewards.
        int proposerIndex = (int)state.GetBeaconProposerIndex();
        int whistleblower = whistleblowerIndex ?? proposerIndex;
        ulong whistleblowerReward = validator.EffectiveBalance / Presets.WhistleblowerRewardQuotientElectra;
        ulong proposerReward = whistleblowerReward * Presets.ProposerWeight / Presets.WeightDenominator;
        state.IncreaseBalance(proposerIndex, proposerReward);
        state.IncreaseBalance(whistleblower, whistleblowerReward - proposerReward);
    }
}
