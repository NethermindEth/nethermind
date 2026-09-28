// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;

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

    /// <summary>Creates a key from a leading path and an inline key postfix.</summary>
    [SkipLocalsInit]
    internal static TKey CreateKey<TKey>(scoped ReadOnlySpan<byte> prefix, scoped ReadOnlySpan<byte> postfix) where TKey : struct, IPbtKey<TKey>
    {
        if (prefix.IsEmpty) return TKey.Create(postfix);
        Span<byte> key = stackalloc byte[PbtStorageTreeKey.MaxLength];
        prefix.CopyTo(key);
        postfix.CopyTo(key[prefix.Length..]);
        return TKey.Create(key[..(prefix.Length + postfix.Length)]);
    }
}
