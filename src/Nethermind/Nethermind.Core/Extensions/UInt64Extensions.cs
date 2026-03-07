// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Core.Extensions;

public static class UInt64Extensions
{
    /// <summary>
    /// Returns the number of zero-valued bytes within the 64-bit value using the SWAR technique.
    /// </summary>
    public static int CountZeroBytes(this ulong value)
    {
        ulong mask = (value - 0x0101010101010101UL) & ~value & 0x8080808080808080UL;
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
