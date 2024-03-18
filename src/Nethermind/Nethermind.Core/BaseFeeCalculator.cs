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
            return Calculate(parent.BaseFeePerGas, parent.GasUsed, parent.GasLimit, parent.Number, specFor1559);
        }

        public static UInt256 Calculate(UInt256 parentBaseFeePerGas, long parentGasUsed, long parentGasLimit, long parentNumber, IEip1559Spec specFor1559)
        {
            UInt256 expectedBaseFee = parentBaseFeePerGas;
            if (specFor1559.IsEip1559Enabled)
            {
                UInt256 parentBaseFee = parentBaseFeePerGas;
                long gasDelta;
                UInt256 feeDelta;
                bool isForkBlockNumber = specFor1559.Eip1559TransitionBlock == parentNumber + 1;
                long parentGasTarget = parentGasLimit / specFor1559.ElasticityMultiplier;
                if (isForkBlockNumber)
                    parentGasTarget = parentGasLimit;

                if (parentGasUsed == parentGasTarget)
                {
                    expectedBaseFee = parentBaseFeePerGas;
                }
                else if (parentGasUsed > parentGasTarget)
                {
                    gasDelta = parentGasUsed - parentGasTarget;
                    feeDelta = UInt256.Max(
                        parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / specFor1559.BaseFeeMaxChangeDenominator,
                        UInt256.One);
                    expectedBaseFee = parentBaseFee + feeDelta;
                }
                else
                {
                    gasDelta = parentGasTarget - parentGasUsed;
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
