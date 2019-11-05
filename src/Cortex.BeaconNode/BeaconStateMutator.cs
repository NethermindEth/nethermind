using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode
{
    public class BeaconStateMutator
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<RewardsAndPenalties> _rewardsAndPenaltiesOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconStateMutator(IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions,
            ICryptographyService cryptographyService,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor)
        {
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _rewardsAndPenaltiesOptions = rewardsAndPenaltiesOptions;
        }

        /// <summary>
        /// Slash the validator with index ``slashed_index``.
        /// </summary>
        public void SlashValidator(BeaconState state, ValidatorIndex slashedIndex, ValidatorIndex whistleblowerIndex)
        {
            var epoch = _beaconStateAccessor.GetCurrentEpoch(state);
            InitiateValidatorExit(state, slashedIndex);
            var validator = state.Validators[(int)(ulong)slashedIndex];
            validator.SetSlashed();
            var slashedWithdrawableEpoch = epoch + _stateListLengthOptions.CurrentValue.EpochsPerSlashingsVector;
            var withdrawableEpoch = Epoch.Max(validator.WithdrawableEpoch, slashedWithdrawableEpoch);
            validator.SetWithdrawableEpoch(withdrawableEpoch);
            
            var slashingsIndex = epoch % _stateListLengthOptions.CurrentValue.EpochsPerSlashingsVector;
            state.AddSlashings(slashingsIndex, validator.EffectiveBalance);

            throw new NotImplementedException();
        }

        public void InitiateValidatorExit(BeaconState state, ValidatorIndex slashedIndex)
        {
            throw new NotImplementedException();
        }
    }
}
