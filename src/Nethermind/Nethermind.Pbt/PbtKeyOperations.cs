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

        int bit = startBit;
        int firstCompleteByte = Math.Min((bit + 7) & ~7, commonBits);
        while (bit < firstCompleteByte)
        {
            if (((bytes[bit >> 3] ^ other[bit >> 3]) & (1 << (7 - (bit & 7)))) != 0) return bit;
            bit++;
        }

        int completeByteEnd = commonBits >> 3;
        for (int byteIndex = bit >> 3; byteIndex < completeByteEnd; byteIndex++)
        {
            int difference = bytes[byteIndex] ^ other[byteIndex];
            if (difference != 0) return (byteIndex << 3) + (BitOperations.LeadingZeroCount((uint)difference) - 24);
        }

        bit = completeByteEnd << 3;
        while (bit < commonBits)
        {
            if (((bytes[bit >> 3] ^ other[bit >> 3]) & (1 << (7 - (bit & 7)))) != 0) return bit;
            bit++;
        }

        return commonBits;
    }

}
