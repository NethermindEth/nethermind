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

using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class ValidatorAssignments
    {
        private readonly ILogger<ValidatorAssignments> _logger;
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;

        public ValidatorAssignments(ILogger<ValidatorAssignments> logger,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor)
        {
            _logger = logger;
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
    }
}