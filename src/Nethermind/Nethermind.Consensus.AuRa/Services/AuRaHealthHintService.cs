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
// 

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.Validators;

namespace Nethermind.Consensus.AuRa.Services
{
    public class AuraHealthHintService : IHealthHintService
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private readonly IValidatorStore _validatorStore;
        
        public AuraHealthHintService(
            IAuRaStepCalculator auRaStepCalculator,
            IValidatorStore validatorStore)
        {
            _auRaStepCalculator = auRaStepCalculator;
            _validatorStore = validatorStore;
        }
        
        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            return CurrentStepDuration() * HealthHintConstants.ProcessingSafetyMultiplier;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            return (ulong)Math.Max(_validatorStore.GetValidators().Length, 1) * CurrentStepDuration() * HealthHintConstants.ProducingSafetyMultiplier;
        }

        private uint CurrentStepDuration()
        {
            return Math.Max((uint)_auRaStepCalculator.CurrentStepDuration, 1);
        }
    }
}
