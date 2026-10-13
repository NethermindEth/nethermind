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
            ulong parentGasTarget = parent.GasLimit / specFor1559.ElasticityMultiplier;
            if (isForkBlockNumber)
                parentGasTarget = parent.GasLimit;

            // EIP-8198 replaces the 1/denominator maximum change with 5/48, multiplying before dividing.
            bool isEip8198 = specFor1559.IsEip8198Enabled;
            UInt256 maxChangeDenominator = isEip8198 ? Eip8198Constants.BaseFeeMaxChangeDenominator : specFor1559.BaseFeeMaxChangeDenominator;

            if (parent.GasUsed == parentGasTarget)
            {
                expectedBaseFee = parent.BaseFeePerGas;
            }
            else if (parentGasTarget == 0 || maxChangeDenominator.IsZero)
            {
                expectedBaseFee = parentBaseFee;
            }
            else if (parent.GasUsed > parentGasTarget)
            {
                ulong gasDelta = parent.GasUsed - parentGasTarget;
                UInt256 feeDelta = UInt256.Max(
                    MaxChange(parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget, maxChangeDenominator, isEip8198),
                    UInt256.One);
                expectedBaseFee = parentBaseFee + feeDelta;
            }
            else
            {
                ulong gasDelta = parentGasTarget - parent.GasUsed;
                UInt256 feeDelta = MaxChange(parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget, maxChangeDenominator, isEip8198);
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

    private static UInt256 MaxChange(in UInt256 targetFeeGasDelta, in UInt256 denominator, bool isEip8198) =>
        isEip8198
            ? targetFeeGasDelta * Eip8198Constants.BaseFeeMaxChangeNumerator / denominator
            : targetFeeGasDelta / denominator;
}

public static class BaseFeeCalculator
{
    public static UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559) =>
        specFor1559.BaseFeeCalculator.Calculate(parent, specFor1559);
}
