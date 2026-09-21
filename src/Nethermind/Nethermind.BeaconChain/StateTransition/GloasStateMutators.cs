// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Slashing over <see cref="BeaconStateGloas"/>: the one <see cref="BeaconStateMutators"/> entry
/// the Gloas block operations need that <see cref="GloasStateAccessors"/> does not already carry.
/// </summary>
/// <remarks>
/// Same Electra semantics as <see cref="BeaconStateMutators.SlashValidator"/> (the pinned Gloas
/// spec leaves <c>slash_validator</c> unmodified); duplicated rather than shared for the reason
/// given on <see cref="GloasStateAccessors"/>. The exit it initiates draws on the Gloas exit churn
/// (EIP-8061) through <see cref="GloasStateAccessors.InitiateValidatorExit"/>.
/// </remarks>
public static class GloasStateMutators
{
    /// <summary>
    /// Spec <c>slash_validator</c>: initiates the validator's exit, marks it slashed with the
    /// extended withdrawability delay, applies the slashing penalty, and credits proposer and
    /// whistleblower rewards.
    /// </summary>
    /// <param name="whistleblowerIndex">Reward recipient; the current proposer when null.</param>
    public static void SlashValidator(this BeaconStateGloas state, int slashedIndex, EpochCache cache, int? whistleblowerIndex = null)
    {
        ulong epoch = state.GetCurrentEpoch();
        state.InitiateValidatorExit(slashedIndex, cache);

        Validator validator = state.Validators![slashedIndex].Clone();
        validator.Slashed = true;
        validator.WithdrawableEpoch = Math.Max(validator.WithdrawableEpoch, GloasStateAccessors.CheckedEpochSum(epoch, Presets.EpochsPerSlashingsVector));
        state.Validators[slashedIndex] = validator;

        state.Slashings![(int)(epoch % Presets.EpochsPerSlashingsVector)] += validator.EffectiveBalance;
        state.DecreaseBalance(slashedIndex, validator.EffectiveBalance / Presets.MinSlashingPenaltyQuotientElectra);

        int proposerIndex = (int)state.GetBeaconProposerIndex();
        int whistleblower = whistleblowerIndex ?? proposerIndex;
        ulong whistleblowerReward = validator.EffectiveBalance / Presets.WhistleblowerRewardQuotientElectra;
        ulong proposerReward = whistleblowerReward * Presets.ProposerWeight / Presets.WeightDenominator;
        state.IncreaseBalance(proposerIndex, proposerReward);
        state.IncreaseBalance(whistleblower, whistleblowerReward - proposerReward);
    }
}
