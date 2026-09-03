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

    public int BitCount { get; }
    public ReadOnlySpan<byte> Bytes => _bytes;

    public int GetBit(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= BitCount) throw new ArgumentOutOfRangeException(nameof(index));
        return (_bytes[index >> 3] >> (7 - (index & 7))) & 1;
    }

    public static PbtBitPrefix FromKey(PbtFullKey key, int startBit, int bitCount)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(startBit);
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        if (startBit > key.BitLength - bitCount) throw new ArgumentOutOfRangeException(nameof(bitCount));
        byte[] bytes = new byte[ByteCount(bitCount)];
        for (int i = 0; i < bitCount; i++)
        {
            if (key.GetBit(startBit + i) != 0) bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        }

        return new PbtBitPrefix(bytes, bitCount);
    }

    internal static PbtBitPrefix Concat(PbtBitPrefix first, int direction, PbtBitPrefix second)
    {
        if ((uint)direction > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        int bitCount = checked(first.BitCount + 1 + second.BitCount);
        if (bitCount > MaxBitCount) throw new InvalidDataException("Merged branch prefix exceeds 65535 bits.");
        byte[] bytes = new byte[ByteCount(bitCount)];
        CopyBits(first, bytes, 0);
        if (direction != 0) bytes[first.BitCount >> 3] |= (byte)(1 << (7 - (first.BitCount & 7)));
        CopyBits(second, bytes, first.BitCount + 1);
        return new PbtBitPrefix(bytes, bitCount);
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

    private static void CopyBits(PbtBitPrefix source, Span<byte> destination, int destinationOffset)
    {
        for (int i = 0; i < source.BitCount; i++)
        {
            if (source.GetBit(i) != 0)
            {
                int bit = destinationOffset + i;
                destination[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
            }
        }
    }
}
