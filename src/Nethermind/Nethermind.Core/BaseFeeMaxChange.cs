// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>Derives the maximum per-block base fee change, <c>numerator / denominator</c>, for a fork.</summary>
public static class BaseFeeMaxChange
{
    /// <summary>
    /// The reduced fraction <c>(baseNumerator × designSlotDurationMs) / (baseDenominator × referenceSlotDurationMs)</c>.
    /// </summary>
    /// <remarks>
    /// Keeps the per-second linear change rate of the reference rule, by default EIP-1559's 1/8 per 12 s slot, for slots
    /// of <paramref name="designSlotDurationMs"/>: 10 000 ms gives 5/48 (EIP-8198), 8 000 ms gives 1/12.
    /// A fork constant, not derived from a chain's slot duration schedule, since it is consensus.
    /// </remarks>
    public static (UInt256 Numerator, UInt256 Denominator) ScaledToSlot(
        ulong designSlotDurationMs,
        ulong referenceSlotDurationMs = 12_000,
        ulong baseNumerator = 1,
        ulong baseDenominator = 8)
    {
        ArgumentOutOfRangeException.ThrowIfZero(designSlotDurationMs);
        ArgumentOutOfRangeException.ThrowIfZero(referenceSlotDurationMs);
        ArgumentOutOfRangeException.ThrowIfZero(baseNumerator);
        ArgumentOutOfRangeException.ThrowIfZero(baseDenominator);

        ulong numerator = checked(baseNumerator * designSlotDurationMs);
        ulong denominator = checked(baseDenominator * referenceSlotDurationMs);
        ulong divisor = GreatestCommonDivisor(numerator, denominator);
        return (numerator / divisor, denominator / divisor);
    }

    private static ulong GreatestCommonDivisor(ulong a, ulong b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
