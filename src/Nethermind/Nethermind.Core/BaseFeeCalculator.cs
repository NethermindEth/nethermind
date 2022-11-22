// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
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
                        parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / Eip1559Constants.BaseFeeMaxChangeDenominator,
                        UInt256.One);
                    expectedBaseFee = parentBaseFee + feeDelta;
                }
                else
                {
                    gasDelta = parentGasTarget - parent.GasUsed;
                    feeDelta = parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / Eip1559Constants.BaseFeeMaxChangeDenominator;
                    expectedBaseFee = UInt256.Max(parentBaseFee - feeDelta, 0);
                }

                if (isForkBlockNumber)
                {
                    expectedBaseFee = Eip1559Constants.ForkBaseFee;
                }

                if (spec.Eip1559BaseFeeMinValue.HasValue)
                {
                    expectedBaseFee = UInt256.Max(expectedBaseFee, spec.Eip1559BaseFeeMinValue.Value);
                }
            }

            return expectedBaseFee;
        }
    }
}
