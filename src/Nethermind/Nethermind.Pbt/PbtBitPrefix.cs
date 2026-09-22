// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>An immutable, MSB-first bit string used by canonical compressed branches.</summary>
public sealed class PbtBitPrefix : IEquatable<PbtBitPrefix>
{
    public const int MaxBitCount = ushort.MaxValue;
    private readonly byte[] _bytes;

    public PbtBitPrefix(ReadOnlySpan<byte> bytes, int bitCount)
    {
        if ((uint)bitCount > MaxBitCount) throw new ArgumentOutOfRangeException(nameof(bitCount));
        int byteCount = ByteCount(bitCount);
        if (bytes.Length != byteCount) throw new ArgumentException("Prefix length does not match its bit count.", nameof(bytes));
        if (bitCount % 8 != 0 && byteCount != 0 && (bytes[^1] & (0xFF >> (bitCount % 8))) != 0)
        {
            throw new ArgumentException("Unused prefix bits must be zero.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
        BitCount = bitCount;
    }

    private PbtBitPrefix(byte[] bytes, int bitCount)
    {
        _bytes = bytes;
        BitCount = bitCount;
    }

    public int BitCount { get; }
    public ReadOnlySpan<byte> Bytes => _bytes;

    public int GetBit(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= BitCount) throw new ArgumentOutOfRangeException(nameof(index));
        return (_bytes[index >> 3] >> (7 - (index & 7))) & 1;
    }

    public bool Equals(PbtBitPrefix? other) =>
        other is not null && BitCount == other.BitCount && Bytes.SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is PbtBitPrefix other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(BitCount);
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    internal static int ByteCount(int bitCount) => (bitCount + 7) >> 3;

    /// <summary>Copies an MSB-first bit range into a zeroed destination range, preserving adjacent bits.</summary>
    internal static void CopyBits(ReadOnlySpan<byte> source, int sourceOffset, int bitCount, Span<byte> destination, int destinationOffset)
    {
        int sourceShift = sourceOffset & 7;
        int destinationShift = destinationOffset & 7;
        int sourceByte = sourceOffset >> 3;
        int destinationByte = destinationOffset >> 3;
        if (sourceShift == 0 && destinationShift == 0)
        {
            int wholeBytes = bitCount >> 3;
            source.Slice(sourceByte, wholeBytes).CopyTo(destination[destinationByte..]);
            sourceByte += wholeBytes;
            destinationByte += wholeBytes;
            bitCount &= 7;
        }

        for (; bitCount > 0; bitCount -= 8, sourceByte++, destinationByte++)
        {
            int count = Math.Min(8, bitCount);
            int value = (source[sourceByte] << sourceShift) & 0xFF;
            if (sourceShift + count > 8) value |= source[sourceByte + 1] >> (8 - sourceShift);
            value &= 0xFF << (8 - count);
            destination[destinationByte] |= (byte)(value >> destinationShift);
            if (destinationShift + count > 8)
                destination[destinationByte + 1] |= (byte)(value << (8 - destinationShift));
        }
    }
}
