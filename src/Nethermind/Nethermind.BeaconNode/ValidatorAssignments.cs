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

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class ValidatorAssignments
    {
        private readonly ILogger<ValidatorAssignments> _logger;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;

        public ValidatorAssignments(ILogger<ValidatorAssignments> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor)
        {
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
        }
        
        public bool CheckIfValidatorActive(BeaconState state, ValidatorIndex validatorIndex)
        {
            Validator validator = state.Validators[(int)validatorIndex];
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            bool isActive = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            return isActive;
        }

        /// <summary>
        /// Return the committee assignment in the ``epoch`` for ``validator_index``.
        /// ``assignment`` returned is a tuple of the following form:
        /// * ``assignment[0]`` is the list of validators in the committee
        /// * ``assignment[1]`` is the index to which the committee is assigned
        ///     * ``assignment[2]`` is the slot at which the committee is assigned
        ///     Return None if no assignment.
        /// </summary>
        public CommitteeAssignment GetCommitteeAssignments(BeaconState state, Epoch epoch, ValidatorIndex validatorIndex)
        {
            Epoch nextEpoch = _beaconStateAccessor.GetCurrentEpoch(state) + Epoch.One;
            if (epoch > nextEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch, $"Committee epoch cannot be greater than next epoch {nextEpoch}.");
            }

            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            ulong endSlot = startSlot + _timeParameterOptions.CurrentValue.SlotsPerEpoch;
            for (Slot slot = startSlot; slot < endSlot; slot += Slot.One)
            {
                ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, slot);
                for (CommitteeIndex index = CommitteeIndex.Zero; index < new CommitteeIndex(committeeCount); index += CommitteeIndex.One)
                {
                    IReadOnlyList<ValidatorIndex> committee = _beaconStateAccessor.GetBeaconCommittee(state, slot, index);
                    if (committee.Contains(validatorIndex))
                    {
                        CommitteeAssignment committeeAssignment = new CommitteeAssignment(committee, index, slot);
                        return committeeAssignment;
                    }
                }
            }

            return CommitteeAssignment.None;
        }

        public bool IsProposer(BeaconState state, ValidatorIndex validatorIndex)
        {
            ValidatorIndex stateProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            return stateProposerIndex.Equals(validatorIndex);
        }
    }
}