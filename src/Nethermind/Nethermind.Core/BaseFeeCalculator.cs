// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core
{
    /// <summary>Calculate BaseFee based on block parent and release spec.</summary>
    public static class BaseFeeCalculator
    {
        public static UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559)
        {
            return Calculate(parent.BaseFeePerGas, parent.GasLimit, parent.Number, parent.GasUsed, specFor1559);
        }

        public static UInt256 Calculate(UInt256 baseFeePerGas, long gasLimit, long number, long gasUsed, IEip1559Spec specFor1559)
        {
            UInt256 expectedBaseFee = baseFeePerGas;
            if (specFor1559.IsEip1559Enabled)
            {
                UInt256 parentBaseFee = baseFeePerGas;
                long gasDelta;
                UInt256 feeDelta;
                bool isForkBlockNumber = specFor1559.Eip1559TransitionBlock == number + 1;
                long parentGasTarget = gasLimit / specFor1559.ElasticityMultiplier;
                if (isForkBlockNumber)
                    parentGasTarget = gasLimit;

                if (gasUsed == parentGasTarget)
                {
                    expectedBaseFee = baseFeePerGas;
                }
                else if (gasUsed > parentGasTarget)
                {
                    gasDelta = gasUsed - parentGasTarget;
                    feeDelta = UInt256.Max(
                        parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / specFor1559.BaseFeeMaxChangeDenominator,
                        UInt256.One);
                    expectedBaseFee = parentBaseFee + feeDelta;
                }
                else
                {
                    gasDelta = parentGasTarget - gasUsed;
                    feeDelta = parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / specFor1559.BaseFeeMaxChangeDenominator;
                    expectedBaseFee = UInt256.Max(parentBaseFee - feeDelta, 0);
                }

                if (isForkBlockNumber)
                {
                    expectedBaseFee = specFor1559.ForkBaseFee;
                }

                if (specFor1559.Eip1559BaseFeeMinValue.HasValue)
                {
                    expectedBaseFee = UInt256.Max(expectedBaseFee, specFor1559.Eip1559BaseFeeMinValue.Value);
                }
            }

            return expectedBaseFee;
        }
    }
}
