// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>An immutable complete EIP-8297 tree key.</summary>
public sealed class PbtFullKey : IEquatable<PbtFullKey>, IComparable<PbtFullKey>
{
    public const int MaxLength = 8192;
    private readonly byte[] _bytes;

    public PbtFullKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), $"Key length must be between 1 and {MaxLength} bytes.");
        }

        _bytes = bytes.ToArray();
    }

    public int Length => _bytes.Length;
    public int BitLength => checked(_bytes.Length * 8);
    public ReadOnlySpan<byte> Bytes => _bytes;

    public int GetBit(int bitIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitIndex);
        if (bitIndex >= BitLength) throw new ArgumentOutOfRangeException(nameof(bitIndex));
        return (_bytes[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1;
    }

    public bool IsPrefixOf(PbtFullKey other) =>
        Length <= other.Length && other.Bytes[..Length].SequenceEqual(_bytes);

    public int FirstDifferingBit(PbtFullKey other, int startBit = 0)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentOutOfRangeException.ThrowIfNegative(startBit);
        int commonBits = Math.Min(BitLength, other.BitLength);
        if (startBit > commonBits) throw new ArgumentOutOfRangeException(nameof(startBit));

        int bit = startBit;
        int firstCompleteByte = Math.Min((bit + 7) & ~7, commonBits);
        while (bit < firstCompleteByte)
        {
            if (((_bytes[bit >> 3] ^ other._bytes[bit >> 3]) & (1 << (7 - (bit & 7)))) != 0) return bit;
            bit++;
        }

        int completeByteEnd = commonBits >> 3;
        for (int byteIndex = bit >> 3; byteIndex < completeByteEnd; byteIndex++)
        {
            int difference = _bytes[byteIndex] ^ other._bytes[byteIndex];
            if (difference != 0) return (byteIndex << 3) + (BitOperations.LeadingZeroCount((uint)difference) - 24);
        }

        bit = completeByteEnd << 3;
        while (bit < commonBits)
        {
            if (((_bytes[bit >> 3] ^ other._bytes[bit >> 3]) & (1 << (7 - (bit & 7)))) != 0) return bit;
            bit++;
        }

        return commonBits;
    }

    public int CompareTo(PbtFullKey? other) => other is null ? 1 : Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtFullKey? other) => other is not null && Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtFullKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }
}
