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
            ulong gasDelta;
            UInt256 feeDelta;
            bool isForkBlockNumber = specFor1559.Eip1559TransitionBlock == parent.Number + 1;
            // parent.GasLimit/GasUsed are stored as signed long in header; cast to ulong for EIP-1559 math
            ulong parentGasLimit = (ulong)parent.GasLimit;
            ulong parentGasUsed = (ulong)parent.GasUsed;
            ulong parentGasTarget = parentGasLimit / (ulong)specFor1559.ElasticityMultiplier;
            if (isForkBlockNumber)
                parentGasTarget = parentGasLimit;

            if (parentGasUsed == parentGasTarget)
            {
                expectedBaseFee = parent.BaseFeePerGas;
            }
            else if (parentGasUsed > parentGasTarget)
            {
                gasDelta = parentGasUsed - parentGasTarget;
                feeDelta = UInt256.Max(
                    parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / (UInt256)specFor1559.BaseFeeMaxChangeDenominator,
                    UInt256.One);
                expectedBaseFee = parentBaseFee + feeDelta;
            }
            else
            {
                gasDelta = parentGasTarget - parentGasUsed;
                feeDelta = parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / (UInt256)specFor1559.BaseFeeMaxChangeDenominator;
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
