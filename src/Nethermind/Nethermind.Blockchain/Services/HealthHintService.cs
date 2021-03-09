//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Blockchain.Services
{
    public class HealthHintService : IHealthHintService
    {
        private readonly ChainSpec _chainSpec;
        
        public HealthHintService(ChainSpec chainSpec)
        {
            _chainSpec = chainSpec;
        }
        
        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            ulong? blockProcessorHint;
            if (_chainSpec.SealEngineType == SealEngineType.Ethash)
                blockProcessorHint = HealthHintConstants.EthashStandardProcessingPeriod * HealthHintConstants.EthashProcessingSafetyMultiplier;
            else
                blockProcessorHint = HealthHintConstants.InfinityHint;
               
            return blockProcessorHint;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            return HealthHintConstants.InfinityHint;
        }
    }
}
