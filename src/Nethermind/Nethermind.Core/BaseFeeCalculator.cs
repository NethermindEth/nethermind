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

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core
{
    /// <summary>Calculate BaseFee based on block parent and release spec.</summary>
    public static class BaseFeeCalculator
    {
        public static UInt256 Calculate(BlockHeader parent, IReleaseSpec spec)
        {
            UInt256 expectedBaseFee = parent.BaseFeePerGas;
            if (spec.IsEip1559Enabled)
            {
                UInt256 parentBaseFee = parent.BaseFeePerGas;
                long gasDelta;
                UInt256 feeDelta;
                bool isForkBlockNumber = spec.Eip1559TransitionBlock == parent.Number + 1;
                long parentGasTarget = parent.GasLimit / Eip1559Constants.ElasticityMultiplier;
                if (isForkBlockNumber)
                    parentGasTarget = parent.GasLimit;
                
                if (parent.GasUsed == parentGasTarget)
                {
                    expectedBaseFee = parent.BaseFeePerGas;
                }
                else if (parent.GasUsed > parentGasTarget)
                {
                    gasDelta = parent.GasUsed - parentGasTarget;
                    feeDelta = UInt256.Max(
                        parentBaseFee * (UInt256) gasDelta / (UInt256) parentGasTarget / Eip1559Constants.BaseFeeMaxChangeDenominator,
                        UInt256.One);
                    expectedBaseFee = parentBaseFee + feeDelta;
                }
                else
                {
                    gasDelta = parentGasTarget - parent.GasUsed;
                    feeDelta = parentBaseFee * (UInt256) gasDelta / (UInt256) parentGasTarget / Eip1559Constants.BaseFeeMaxChangeDenominator;
                    expectedBaseFee = UInt256.Max(parentBaseFee - feeDelta, 0);
                }

                if (isForkBlockNumber)
                {
                    expectedBaseFee = Eip1559Constants.ForkBaseFee;
                }
            }

            return expectedBaseFee;
        }
    }
}
