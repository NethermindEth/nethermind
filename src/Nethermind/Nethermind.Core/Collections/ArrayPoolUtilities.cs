// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

internal static class ArrayPoolUtilities
{
    /// <summary>Returns an allocation capacity for a positive minimum length.</summary>
    /// <remarks>
    /// The capacity is the next power of two unless it would exceed the signed array-length range.
    /// Requests above <c>2^30</c> are therefore returned unchanged, so rounding cannot make an
    /// otherwise representable array length overflow.
    /// </remarks>
    /// <param name="minimumLength">The positive minimum allocation length.</param>
    /// <returns>A capacity greater than or equal to <paramref name="minimumLength"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimumLength"/> is not positive.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetPowerOfTwoCapacity(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumLength);
        uint capacity = BitOperations.RoundUpToPowerOf2((uint)minimumLength);

        return capacity <= int.MaxValue ? (int)capacity : minimumLength;
    }
}
