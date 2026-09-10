// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

internal static class PbtKeyOperations
{
    internal static int GetBit(ReadOnlySpan<byte> bytes, int bitIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitIndex);
        if (bitIndex >= bytes.Length * 8) throw new ArgumentOutOfRangeException(nameof(bitIndex));
        return (bytes[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1;
    }

    internal static int FirstDifferingBit(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> other, int startBit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startBit);
        int commonBits = Math.Min(bytes.Length, other.Length) * 8;
        if (startBit > commonBits) throw new ArgumentOutOfRangeException(nameof(startBit));

        int byteIndex = startBit >> 3;
        int bitOffset = startBit & 7;
        if (bitOffset != 0)
        {
            uint difference = (uint)((bytes[byteIndex] ^ other[byteIndex]) & (0xFF >> bitOffset));
            if (difference != 0) return (byteIndex << 3) + BitOperations.LeadingZeroCount(difference) - 24;
            byteIndex++;
        }

        byteIndex += bytes[byteIndex..].CommonPrefixLength(other[byteIndex..]);
        if (byteIndex == commonBits >> 3) return commonBits;
        return (byteIndex << 3) + BitOperations.LeadingZeroCount((uint)(bytes[byteIndex] ^ other[byteIndex])) - 24;
    }

}
