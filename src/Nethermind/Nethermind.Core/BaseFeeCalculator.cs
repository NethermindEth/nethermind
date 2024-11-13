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
            long gasDelta;
            UInt256 feeDelta;
            bool isForkBlockNumber = specFor1559.Eip1559TransitionBlock == parent.Number + 1;
            long parentGasTarget = parent.GasLimit / specFor1559.ElasticityMultiplier;
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
                    parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget / specFor1559.BaseFeeMaxChangeDenominator,
                    UInt256.One);
                expectedBaseFee = parentBaseFee + feeDelta;
            }
            else
            {
                gasDelta = parentGasTarget - parent.GasUsed;
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

/// <remarks>
/// This class is a hack to support custom base fee calculations while still using a Singleton
/// Due to the extensive use of `BaseFeeCalculator.Calculate` in the codebase, it is not feasible to pass the calculator as a parameter.
/// Thus, for now we will use a Singleton pattern to allow for custom base fee calculations.
///
/// When required, plugins can call <see cref="Override"/> to modify the global <see cref="IBaseFeeCalculator"/>
/// </remarks>
public static class BaseFeeCalculator
{
    private static IBaseFeeCalculator _instance = new DefaultBaseFeeCalculator();

    public static void Override(IBaseFeeCalculator calculator) => _instance = calculator;
    public static void Reset() => _instance = new DefaultBaseFeeCalculator();

    public static UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559) => _instance!.Calculate(parent, specFor1559);
}
