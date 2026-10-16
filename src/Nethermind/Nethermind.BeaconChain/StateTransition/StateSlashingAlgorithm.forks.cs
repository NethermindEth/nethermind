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
public static partial class GloasStateMutators
#else
public static partial class BeaconStateMutators
#endif
{
    /// <summary>
    /// Slashes validator <paramref name="slashedIndex"/> with Electra penalties: initiates its exit,
    /// applies the slashing penalty, and credits proposer and whistleblower rewards.
    /// </summary>
    /// <param name="whistleblowerIndex">Reward recipient; the current proposer when null.</param>
    public static partial void SlashValidator(this AccessorState state, int slashedIndex, EpochCache cache, int? whistleblowerIndex)
    {
        ulong epoch = state.GetCurrentEpoch();
        state.InitiateValidatorExit(slashedIndex, cache);

        Validator validator = state.Validators![slashedIndex].Clone();
        validator.Slashed = true;
        validator.WithdrawableEpoch = Math.Max(validator.WithdrawableEpoch,
#if GLOAS
            GloasStateAccessors.CheckedEpochSum(epoch, Presets.EpochsPerSlashingsVector)
#else
            CheckedEpochSum(epoch, Presets.EpochsPerSlashingsVector)
#endif
        );
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
