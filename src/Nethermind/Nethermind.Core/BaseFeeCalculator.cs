// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core;

public interface IBaseFeeCalculator
{
    UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559);
}

/// <summary>
/// Calculate <c>BaseFee</c> based on parent <see cref="BlockHeader"/> and <see cref="IEip1559Spec"/>.
/// </summary>
public sealed class DefaultBaseFeeCalculator : IBaseFeeCalculator
{
    public UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559)
    {
        UInt256 expectedBaseFee = parent.BaseFeePerGas;
        if (specFor1559.IsEip1559Enabled)
        {
            UInt256 parentBaseFee = parent.BaseFeePerGas;
            bool isForkBlockNumber = specFor1559.Eip1559TransitionBlock == parent.Number + 1;
            long parentGasTarget = parent.GasLimit / specFor1559.ElasticityMultiplier;
            if (isForkBlockNumber)
                parentGasTarget = parent.GasLimit;

            if (parent.GasUsed == parentGasTarget)
            {
                expectedBaseFee = parent.BaseFeePerGas;
            }
            else if (parentGasTarget == 0 || specFor1559.BaseFeeMaxChangeDenominator.IsZero)
            {
                expectedBaseFee = parentBaseFee;
            }
            else if (parent.GasUsed > parentGasTarget)
            {
                long gasDelta = parent.GasUsed - parentGasTarget;
                UInt256 feeDelta = UInt256.Max(
                    parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / specFor1559.BaseFeeMaxChangeDenominator,
                    UInt256.One);
                expectedBaseFee = parentBaseFee + feeDelta;
            }
            else
            {
                long gasDelta = parentGasTarget - parent.GasUsed;
                UInt256 feeDelta = parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / specFor1559.BaseFeeMaxChangeDenominator;
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

public static class BaseFeeCalculator
{
    public static UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559) =>
        specFor1559.BaseFeeCalculator.Calculate(parent, specFor1559);
}
