// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core;

public interface IBaseFeeCalculator
{
    /// <summary>
    /// The base fee of the child of <paramref name="parent"/> at <paramref name="timestamp"/> and <paramref name="slotNumber"/>.
    /// </summary>
    /// <remarks>The child's timestamp and slot number only matter under EIP-8198.</remarks>
    UInt256 Calculate(BlockHeader parent, ulong timestamp, ulong? slotNumber, IEip1559Spec specFor1559);
}

/// <summary>
/// Calculate <c>BaseFee</c> based on parent <see cref="BlockHeader"/> and <see cref="IEip1559Spec"/>.
/// </summary>
/// <remarks>
/// Under EIP-8198 the maximum per-block change is <c>seconds_elapsed / (BaseFeeMaxChangeDenominator × 12 × slots_elapsed)</c>,
/// the slot duration read from the spacing of the child and its parent over 96: 1/8 at 12 s slots and 5/48 at 10 s slots,
/// capped at the EIP-1559 maximum change (EIP-8198). The multiplication comes before the division.
/// </remarks>
public sealed class DefaultBaseFeeCalculator : IBaseFeeCalculator
{
    public UInt256 Calculate(BlockHeader parent, ulong timestamp, ulong? slotNumber, IEip1559Spec specFor1559)
    {
        UInt256 expectedBaseFee = parent.BaseFeePerGas;
        if (specFor1559.IsEip1559Enabled)
        {
            UInt256 parentBaseFee = parent.BaseFeePerGas;
            bool isForkBlockNumber = specFor1559.Eip1559TransitionBlock == parent.Number + 1;
            ulong parentGasTarget = parent.GasLimit / specFor1559.ElasticityMultiplier;
            if (isForkBlockNumber)
                parentGasTarget = parent.GasLimit;

            UInt256 maxChangeNumerator = UInt256.One;
            UInt256 maxChangeDenominator = specFor1559.BaseFeeMaxChangeDenominator;
            if (specFor1559.IsEip8198Enabled && TryGetSlotSpacing(parent, timestamp, slotNumber, out ulong secondsElapsed, out ulong slotsElapsed))
            {
                UInt256 referenceSeconds = (UInt256)Eip8198Constants.BaseFeeReferenceSlotSeconds * slotsElapsed;
                maxChangeDenominator *= referenceSeconds;
                maxChangeNumerator = UInt256.Min(secondsElapsed, referenceSeconds);
            }

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
                    MaxChange(parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget, maxChangeNumerator, maxChangeDenominator),
                    UInt256.One);
                expectedBaseFee = parentBaseFee + feeDelta;
            }
            else
            {
                ulong gasDelta = parentGasTarget - parent.GasUsed;
                UInt256 feeDelta = MaxChange(parentBaseFee * (UInt256)gasDelta / (UInt256)parentGasTarget, maxChangeNumerator, maxChangeDenominator);
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

    /// <summary>
    /// The spacing of the child from <paramref name="parent"/>; false when the child is not later in both time and slot,
    /// or either slot number is missing.
    /// </summary>
    /// <remarks>
    /// Header validation rejects a child that is not later in both under EIP-8198, so the fallback only prices a block that
    /// fails validation anyway, or one built on a parent without a slot number (EIP-7843 not yet active), where it keeps the
    /// EIP-1559 rule, the spacing of a 12 s slot.
    /// </remarks>
    private static bool TryGetSlotSpacing(BlockHeader parent, ulong timestamp, ulong? slotNumber, out ulong secondsElapsed, out ulong slotsElapsed)
    {
        if (slotNumber is { } slot && parent.SlotNumber is { } parentSlot && slot > parentSlot && timestamp > parent.Timestamp)
        {
            secondsElapsed = timestamp - parent.Timestamp;
            slotsElapsed = slot - parentSlot;
            return true;
        }

        secondsElapsed = slotsElapsed = 0;
        return false;
    }

    /// <summary><c>targetFeeGasDelta × numerator / denominator</c>, multiplying first, without overflowing 256 bits.</summary>
    /// <remarks>
    /// Exact as <c>q × numerator + r × numerator / denominator</c> for <c>targetFeeGasDelta = q × denominator + r</c>: the
    /// numerator is capped at the denominator, which stays below 2^128 for any realistic denominator and slot count.
    /// </remarks>
    private static UInt256 MaxChange(in UInt256 targetFeeGasDelta, in UInt256 numerator, in UInt256 denominator)
    {
        if (numerator.IsOne) return targetFeeGasDelta / denominator;

        UInt256.Mod(targetFeeGasDelta, denominator, out UInt256 remainder);
        return targetFeeGasDelta / denominator * numerator + remainder * numerator / denominator;
    }
}

public static class BaseFeeCalculator
{
    /// <summary>The base fee of <paramref name="header"/>, the child of <paramref name="parent"/>.</summary>
    public static UInt256 Calculate(BlockHeader parent, BlockHeader header, IEip1559Spec specFor1559) =>
        Calculate(parent, header.Timestamp, header.SlotNumber, specFor1559);

    /// <summary>The base fee of the child of <paramref name="parent"/> at <paramref name="timestamp"/> and <paramref name="slotNumber"/>.</summary>
    public static UInt256 Calculate(BlockHeader parent, ulong timestamp, ulong? slotNumber, IEip1559Spec specFor1559) =>
        specFor1559.BaseFeeCalculator.Calculate(parent, timestamp, slotNumber, specFor1559);

    /// <summary>
    /// Predicts the base fee of the child of <paramref name="parent"/>, assuming it lands in the slot after the parent's,
    /// which starts at <paramref name="nextSlotTimestamp"/>.
    /// </summary>
    /// <remarks>
    /// Under EIP-8198 only the average slot duration between the two blocks matters, so a later slot of the same duration
    /// gives the same base fee.
    /// </remarks>
    public static UInt256 CalculateForNextSlot(BlockHeader parent, ulong nextSlotTimestamp, IEip1559Spec specFor1559) =>
        Calculate(parent, nextSlotTimestamp, parent.SlotNumber + 1, specFor1559);

    /// <summary>
    /// Predicts the base fee of the child of <paramref name="parent"/> in the slot after the parent's on
    /// <paramref name="slots"/>, or for a <see cref="Eip8198Constants.BaseFeeReferenceSlotSeconds"/> slot without one.
    /// </summary>
    public static UInt256 CalculateForNextSlot(BlockHeader parent, SlotSchedule? slots, IEip1559Spec specFor1559) =>
        slots is null
            ? CalculateForReferenceSlot(parent, specFor1559)
            : CalculateForNextSlot(parent, slots.GetNextSlotTimestamp(parent.Timestamp), specFor1559);

    /// <summary>
    /// Predicts the base fee of the child of <paramref name="parent"/> where the slot duration is unknown, assuming a
    /// <see cref="Eip8198Constants.BaseFeeReferenceSlotSeconds"/> slot.
    /// </summary>
    /// <remarks>
    /// That is the EIP-1559 rule. Under EIP-8198 with shorter slots it overstates the change: up to 1/8 of the parent's base
    /// fee instead of 5/48 at 10 s slots.
    /// </remarks>
    public static UInt256 CalculateForReferenceSlot(BlockHeader parent, IEip1559Spec specFor1559) =>
        CalculateForNextSlot(parent, parent.Timestamp + Eip8198Constants.BaseFeeReferenceSlotSeconds, specFor1559);
}
