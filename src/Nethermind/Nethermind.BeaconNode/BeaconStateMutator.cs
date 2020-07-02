//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class BeaconStateMutator
    {
        private readonly IBeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly ChainConstants _chainConstants;
        private readonly IOptionsMonitor<RewardsAndPenalties> _rewardsAndPenaltiesOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconStateMutator(
            ChainConstants chainConstants,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions,
            IBeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor)
        {
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _chainConstants = chainConstants;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _rewardsAndPenaltiesOptions = rewardsAndPenaltiesOptions;
        }

        /// <summary>
        /// Decrease the validator balance at index ``index`` by ``delta``, with underflow protection.
        /// </summary>
        public void DecreaseBalance(BeaconState state, ValidatorIndex index, Gwei delta)
        {
            Gwei balance = state.Balances[(int)index];
            if (delta > balance)
            {
                state.SetBalance(index, Gwei.Zero);
            }
            else
            {
                Gwei newBalance = balance - delta;
                state.SetBalance(index, newBalance);
            }
        }

        /// <summary>
        /// Increase the validator balance at index ``index`` by ``delta``.
        /// </summary>
        public void IncreaseBalance(BeaconState state, ValidatorIndex index, Gwei delta)
        {
            Gwei balance = state.Balances[(int)index];
            Gwei newBalance = balance + delta;
            state.SetBalance(index, newBalance);
        }

        /// <summary>
        /// Initiate the exit of the validator with index ``index``.
        /// </summary>
        public void InitiateValidatorExit(BeaconState state, ValidatorIndex index)
        {
            // Return if validator already initiated exit
            Validator validator = state.Validators[(int)index];
            if (validator.ExitEpoch != _chainConstants.FarFutureEpoch)
            {
                return;
            }

            // Compute exit queue epoch
            IEnumerable<Epoch> exitEpochs = state.Validators
                .Where(x => x.ExitEpoch != _chainConstants.FarFutureEpoch)
                .Select(x => x.ExitEpoch);
            Epoch maxExitEpoch = exitEpochs.DefaultIfEmpty().Max();
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            Epoch activationExitEpoch = _beaconChainUtility.ComputeActivationExitEpoch(currentEpoch);
            Epoch exitQueueEpoch = Epoch.Max(maxExitEpoch, activationExitEpoch);
            int exitQueueChurn = state.Validators.Count(x => x.ExitEpoch == exitQueueEpoch);
            ulong validatorChurnLimit = _beaconStateAccessor.GetValidatorChurnLimit(state);
            if ((ulong)exitQueueChurn >= validatorChurnLimit)
            {
                exitQueueEpoch += new Epoch(1);
            }

            // Set validator exit epoch and withdrawable epoch
            validator.SetExitEpoch(exitQueueEpoch);
            Epoch withdrawableEpoch = validator.ExitEpoch + _timeParameterOptions.CurrentValue.MinimumValidatorWithdrawabilityDelay;
            validator.SetWithdrawableEpoch(withdrawableEpoch);
        }

        /// <summary>
        /// Slash the validator with index ``slashed_index``.
        /// </summary>
        public void SlashValidator(BeaconState state, ValidatorIndex slashedIndex, ValidatorIndex? optionalWhistleblowerIndex)
        {
            RewardsAndPenalties rewardsAndPenalties = _rewardsAndPenaltiesOptions.CurrentValue;
            StateListLengths stateListLengths = _stateListLengthOptions.CurrentValue;

            Epoch epoch = _beaconStateAccessor.GetCurrentEpoch(state);
            InitiateValidatorExit(state, slashedIndex);
            Validator validator = state.Validators[(int)slashedIndex];
            validator.SetSlashed();
            Epoch slashedWithdrawableEpoch = (Epoch)(epoch + stateListLengths.EpochsPerSlashingsVector);
            Epoch withdrawableEpoch = Epoch.Max(validator.WithdrawableEpoch, slashedWithdrawableEpoch);
            validator.SetWithdrawableEpoch(withdrawableEpoch);

            Epoch slashingsIndex = (Epoch)(epoch % stateListLengths.EpochsPerSlashingsVector);
            state.SetSlashings(slashingsIndex, validator.EffectiveBalance);
            Gwei slashingPenalty = validator.EffectiveBalance / rewardsAndPenalties.MinimumSlashingPenaltyQuotient;
            DecreaseBalance(state, slashedIndex, slashingPenalty);

            // Apply proposer and whistleblower rewards
            ValidatorIndex proposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            ValidatorIndex whistleblowerIndex = optionalWhistleblowerIndex ?? proposerIndex;
            
            Gwei whistleblowerReward = validator.EffectiveBalance / rewardsAndPenalties.WhistleblowerRewardQuotient;
            Gwei proposerReward = whistleblowerReward / rewardsAndPenalties.ProposerRewardQuotient;

            IncreaseBalance(state, proposerIndex, proposerReward);
            IncreaseBalance(state, whistleblowerIndex, whistleblowerReward - proposerReward);
        }
    }
}
