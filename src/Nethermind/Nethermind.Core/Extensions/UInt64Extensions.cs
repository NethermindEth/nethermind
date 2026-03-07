// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Core.Extensions;

public static class UInt64Extensions
{
    /// <summary>
    /// Returns the number of zero-valued bytes within the 64-bit value using the SWAR technique.
    /// Uses the borrow-safe variant: masks off bit 7 before adding 0x7F per byte so no carry
    /// can propagate across byte boundaries, avoiding false positives for 0x01 bytes.
    /// </summary>
    public static int CountZeroBytes(this ulong value)
    {
        // Per-byte: 0x00 → 0x7F + 0x7F = 0x7F | 0x00 | 0x7F = 0x7F → ~0x7F = 0x80
        //           non-zero → at least 0x80 after | value | 0x7F → ~result = 0x00
        // Max per-byte addition is 0x7F + 0x7F = 0xFE < 256, so no carry crosses byte boundaries.
        ulong mask = ~(((value & 0x7F7F7F7F7F7F7F7FUL) + 0x7F7F7F7F7F7F7F7FUL) | value | 0x7F7F7F7F7F7F7F7FUL);
        return BitOperations.PopCount(mask);
    }

    public static ulong ToULongFromBigEndianByteArrayWithoutLeadingZeros(this byte[]? bytes) =>
        ToULongFromBigEndianByteArrayWithoutLeadingZeros(bytes.AsSpan());

    public static ulong ToULongFromBigEndianByteArrayWithoutLeadingZeros(this ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0L;
        }

        ulong value = 0;
        int length = bytes.Length;

        for (int i = 0; i < length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }
}
