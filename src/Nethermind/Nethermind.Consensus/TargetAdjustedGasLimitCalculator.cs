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

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus
{
    public class TargetAdjustedGasLimitCalculator : IGasLimitCalculator
    {
        private readonly ISpecProvider _specProvider;
        private readonly IMiningConfig _miningConfig;

        public TargetAdjustedGasLimitCalculator(ISpecProvider specProvider, IMiningConfig miningConfig)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _miningConfig = miningConfig ?? throw new ArgumentNullException(nameof(miningConfig));
        }
        
        public long GetGasLimit(BlockHeader parentHeader)
        {
            long parentGasLimit = parentHeader.GasLimit;
            long gasLimit = parentGasLimit;
            
            long? targetGasLimit = _miningConfig.TargetBlockGasLimit;
            long newBlockNumber = parentHeader.Number + 1;
            IReleaseSpec spec = _specProvider.GetSpec(newBlockNumber);
            if (targetGasLimit != null)
            {
                long maxGasLimitDifference = Math.Max(0, parentGasLimit / spec.GasLimitBoundDivisor - 1);
                gasLimit = targetGasLimit.Value > parentGasLimit
                    ? parentGasLimit + Math.Min(targetGasLimit.Value - parentGasLimit, maxGasLimitDifference)
                    : parentGasLimit - Math.Min(parentGasLimit - targetGasLimit.Value, maxGasLimitDifference);
            }

            gasLimit = Eip1559GasLimitAdjuster.AdjustGasLimit(spec, gasLimit, newBlockNumber);
            return gasLimit;
        }
    }
}
