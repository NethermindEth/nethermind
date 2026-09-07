// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>An immutable complete EIP-8297 tree key.</summary>
/// <remarks>Keys larger than 66 bytes are unsupported. The default value is not a valid complete key.</remarks>
public readonly struct PbtFullKey : IEquatable<PbtFullKey>, IComparable<PbtFullKey>
{
    public const int MaxLength = 66;
    private readonly KeyBytes _bytes;

    [InlineArray(MaxLength)]
    private struct KeyBytes
    {
        private byte _element0;
    }

    public PbtFullKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), $"Key length must be between 1 and {MaxLength} bytes.");
        }

        bytes.CopyTo(_bytes);
        Length = bytes.Length;
    }

    public int Length { get; }
    public int BitLength => Length * 8;
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes[..Length];

    public int GetBit(int bitIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitIndex);
        if (bitIndex >= BitLength) throw new ArgumentOutOfRangeException(nameof(bitIndex));
        return (_bytes[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1;
    }

    public bool IsPrefixOf(PbtFullKey other) =>
        Length <= other.Length && other.Bytes[..Length].SequenceEqual(Bytes);

    public int FirstDifferingBit(PbtFullKey other, int startBit = 0)
    {
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

    public int CompareTo(PbtFullKey other) => Bytes.SequenceCompareTo(other.Bytes);
    public bool Equals(PbtFullKey other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is PbtFullKey other && Equals(other);
    public static bool operator ==(PbtFullKey left, PbtFullKey right) => left.Equals(right);
    public static bool operator !=(PbtFullKey left, PbtFullKey right) => !left.Equals(right);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }
}
