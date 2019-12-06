using System.Linq;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;
using Epoch = Nethermind.BeaconNode.Containers.Epoch;
using ValidatorIndex = Nethermind.BeaconNode.Containers.ValidatorIndex;

namespace Nethermind.BeaconNode
{
    public class BeaconStateMutator
    {
        private readonly BeaconChainUtility _beaconChainUtility;
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
            BeaconChainUtility beaconChainUtility,
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
            var balance = state.Balances[(int)(ulong)index];
            if (delta > balance)
            {
                state.SetBalance(index, Gwei.Zero);
            }
            else
            {
                var newBalance = balance - delta;
                state.SetBalance(index, newBalance);
            }
        }

        /// <summary>
        /// Increase the validator balance at index ``index`` by ``delta``.
        /// </summary>
        public void IncreaseBalance(BeaconState state, ValidatorIndex index, Gwei delta)
        {
            var balance = state.Balances[(int)(ulong)index];
            var newBalance = balance + delta;
            state.SetBalance(index, newBalance);
        }

        /// <summary>
        /// Initiate the exit of the validator with index ``index``.
        /// </summary>
        public void InitiateValidatorExit(BeaconState state, ValidatorIndex index)
        {
            // Return if validator already initiated exit
            var validator = state.Validators[(int)(ulong)index];
            if (validator.ExitEpoch != _chainConstants.FarFutureEpoch)
            {
                return;
            }

            // Compute exit queue epoch
            var exitEpochs = state.Validators
                .Where(x => x.ExitEpoch != _chainConstants.FarFutureEpoch)
                .Select(x => x.ExitEpoch);
            var maxExitEpoch = exitEpochs.DefaultIfEmpty().Max();
            var currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            var activationExitEpoch = _beaconChainUtility.ComputeActivationExitEpoch(currentEpoch);
            var exitQueueEpoch = Epoch.Max(maxExitEpoch, activationExitEpoch);
            var exitQueueChurn = state.Validators.Where(x => x.ExitEpoch == exitQueueEpoch).Count();
            var validatorChurnLimit = _beaconStateAccessor.GetValidatorChurnLimit(state);
            if ((ulong)exitQueueChurn >= validatorChurnLimit)
            {
                exitQueueEpoch += new Epoch(1);
            }

            // Set validator exit epoch and withdrawable epoch
            validator.SetExitEpoch(exitQueueEpoch);
            var withdrawableEpoch = validator.ExitEpoch + _timeParameterOptions.CurrentValue.MinimumValidatorWithdrawabilityDelay;
            validator.SetWithdrawableEpoch(withdrawableEpoch);
        }

        /// <summary>
        /// Slash the validator with index ``slashed_index``.
        /// </summary>
        public void SlashValidator(BeaconState state, ValidatorIndex slashedIndex, ValidatorIndex whistleblowerIndex)
        {
            var rewardsAndPenalties = _rewardsAndPenaltiesOptions.CurrentValue;
            var stateListLengths = _stateListLengthOptions.CurrentValue;

            var epoch = _beaconStateAccessor.GetCurrentEpoch(state);
            InitiateValidatorExit(state, slashedIndex);
            var validator = state.Validators[(int)(ulong)slashedIndex];
            validator.SetSlashed();
            var slashedWithdrawableEpoch = epoch + stateListLengths.EpochsPerSlashingsVector;
            var withdrawableEpoch = Epoch.Max(validator.WithdrawableEpoch, slashedWithdrawableEpoch);
            validator.SetWithdrawableEpoch(withdrawableEpoch);

            var slashingsIndex = epoch % stateListLengths.EpochsPerSlashingsVector;
            state.SetSlashings(slashingsIndex, validator.EffectiveBalance);
            var slashingPenalty = validator.EffectiveBalance / rewardsAndPenalties.MinimumSlashingPenaltyQuotient;
            DecreaseBalance(state, slashedIndex, slashingPenalty);

            // Apply proposer and whistleblower rewards
            var proposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            if (whistleblowerIndex == ValidatorIndex.None)
            {
                whistleblowerIndex = proposerIndex;
            }
            var whistleblowerReward = validator.EffectiveBalance / rewardsAndPenalties.WhistleblowerRewardQuotient;
            var proposerReward = whistleblowerReward / rewardsAndPenalties.ProposerRewardQuotient;

            IncreaseBalance(state, proposerIndex, proposerReward);
            IncreaseBalance(state, whistleblowerIndex, whistleblowerReward - proposerReward);
        }
    }
}
