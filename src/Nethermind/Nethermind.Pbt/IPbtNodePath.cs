// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Pbt;

/// <summary>A canonical node's structural identity, independent of its in-memory capacity.</summary>
public interface IPbtNodePath<TSelf> : IEquatable<TSelf>, IComparable<TSelf> where TSelf : struct, IPbtNodePath<TSelf>
{
    /// <summary>Gets the maximum supported path depth.</summary>
    static abstract int MaxBitDepth { get; }
    /// <summary>Creates a path, validating its length and unused bits.</summary>
    static abstract TSelf Create(ReadOnlySpan<byte> path, int bitDepth);
    /// <summary>Compares canonical identities across path capacities.</summary>
    int CompareTo<TOther>(TOther other) where TOther : struct, IPbtNodePath<TOther>;
    /// <summary>Tests canonical identity across path capacities.</summary>
    bool Equals<TOther>(TOther other) where TOther : struct, IPbtNodePath<TOther>;
    /// <summary>Gets the number of consumed key bits.</summary>
    int BitDepth { get; }
    /// <summary>Reads a logical path bit in MSB-first order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bit index is outside the logical path.</exception>
    int GetBit(int bitIndex);
    /// <summary>Reads a canonical path byte, including zero padding in the final byte.</summary>
    /// <exception cref="IndexOutOfRangeException">The byte index is outside the canonical bytes.</exception>
    byte GetByte(int byteIndex);
    /// <summary>Copies a logical bit range into a zeroed destination range, preserving adjacent bits.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An offset or count is negative, or a range exceeds the path or destination.</exception>
    void CopyBitsTo(int sourceBitOffset, Span<byte> destination, int destinationBitOffset, int bitCount);
    /// <summary>Tests the first <paramref name="bitCount"/> bits against a key; insufficient path or key length is a mismatch.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bit count is negative.</exception>
    bool MatchesPrefix(ReadOnlySpan<byte> key, int bitCount);
    /// <summary>Returns the first <paramref name="depth"/> bits, preserving the path type and capacity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The depth is negative or exceeds the current path depth.</exception>
    TSelf Prefix(int depth);
    /// <summary>Returns a path with four bits appended, preserving the path type and capacity.</summary>
    /// <param name="nibble">A value from zero to fifteen, appended most significant bit first.</param>
    /// <exception cref="ArgumentOutOfRangeException">The nibble is outside zero to fifteen, or the resulting path exceeds its capacity.</exception>
    TSelf AppendNib(int nibble);
    /// <summary>Appends zero to four right-aligned bits, preserving the path type and capacity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count, bits, or resulting depth is outside its supported range.</exception>
    TSelf AppendBits(int bits, int bitCount);
    /// <summary>Appends a compressed prefix and one direction bit, preserving the path type and capacity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The direction is not zero or one, or the resulting path exceeds its capacity.</exception>
    TSelf Append(CompressedPrefix prefix, int direction);
    /// <summary>Converts this path to the selected capacity without changing its identity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The path exceeds the selected capacity.</exception>
    TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath>;
    /// <summary>Tests the first bits against another path; insufficient length is a mismatch.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bit count is negative.</exception>
    bool MatchesPrefix<TOther>(TOther other, int bitCount) where TOther : struct, IPbtNodePath<TOther>;
    /// <summary>Gets the encoded length of the four-byte depth and canonical path bytes.</summary>
    int EncodedLength { get; }
    /// <summary>Writes the big-endian depth and canonical path bytes, leaving excess destination bytes unchanged.</summary>
    /// <exception cref="ArgumentException">The destination is shorter than <see cref="EncodedLength"/>.</exception>
    void Encode(Span<byte> destination);

    internal static void Validate(ReadOnlySpan<byte> path, int bitDepth, int maximumDepth)
    {
        if ((uint)bitDepth > (uint)maximumDepth) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        int byteLength = (bitDepth + 7) >> 3;
        if (path.Length != byteLength) throw new ArgumentException("Path length does not match the bit depth.", nameof(path));
        if (byteLength != 0 && (bitDepth & 7) != 0 && (path[^1] & (0xFF >> (bitDepth & 7))) != 0)
            throw new ArgumentException("Unused path bits must be zero.", nameof(path));
    }

    internal static void Encode(ReadOnlySpan<byte> path, int bitDepth, Span<byte> destination)
    {
        if (destination.Length < 4 + path.Length)
            throw new ArgumentException("The destination is too short.", nameof(destination));
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)bitDepth);
        path.CopyTo(destination[4..]);
    }

    internal static TPath Decode<TPath>(ReadOnlySpan<byte> encoding) where TPath : struct, IPbtNodePath<TPath>
    {
        if (encoding.Length < 4) throw new InvalidDataException("Truncated PBT node path.");
        uint depth = BinaryPrimitives.ReadUInt32BigEndian(encoding);
        if (depth > TPath.MaxBitDepth || encoding.Length != 4 + ((depth + 7) >> 3))
            throw new InvalidDataException("Invalid PBT node path length.");
        try { return TPath.Create(encoding[4..], (int)depth); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid PBT node path padding.", exception); }
    }

    internal static TPath FromKey<TPath>(ReadOnlySpan<byte> key, int bitDepth) where TPath : struct, IPbtNodePath<TPath>
    {
        if (key.IsEmpty) throw new ArgumentException("A complete key cannot be empty.", nameof(key));
        ArgumentOutOfRangeException.ThrowIfNegative(bitDepth);
        if (bitDepth > key.Length * 8 || bitDepth > TPath.MaxBitDepth) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        Span<byte> path = stackalloc byte[(bitDepth + 7) >> 3];
        key[..path.Length].CopyTo(path);
        if (path.Length != 0 && (bitDepth & 7) != 0) path[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        return TPath.Create(path, bitDepth);
    }

    internal static TSelf Prefix(TSelf path, int depth)
    {
        if ((uint)depth > (uint)path.BitDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        Span<byte> prefix = stackalloc byte[(depth + 7) >> 3];
        prefix.Clear();
        path.CopyBitsTo(0, prefix, 0, depth);
        return TSelf.Create(prefix, depth);
    }

    internal static TPath AppendBits<TPath>(ReadOnlySpan<byte> source, int bitDepth, int bits, int bitCount)
        where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> path = stackalloc byte[TPath.MaxBitDepth >> 3];
        int depth = AppendBits(source, bitDepth, bits, bitCount, path);
        return TPath.Create(path[..((depth + 7) >> 3)], depth);
    }

    private static int AppendBits(ReadOnlySpan<byte> source, int bitDepth, int bits, int bitCount, Span<byte> path)
    {
        if ((uint)bitCount > 4) throw new ArgumentOutOfRangeException(nameof(bitCount));
        if ((uint)bits >= (1u << bitCount)) throw new ArgumentOutOfRangeException(nameof(bits));
        int depth = checked(bitDepth + bitCount);
        if (depth > path.Length * 8) throw new ArgumentOutOfRangeException(nameof(bitCount));
        path.Clear();
        source.CopyTo(path);
        for (int index = 0; index < bitCount; index++)
        {
            int bit = bitDepth + index;
            path[bit >> 3] |= (byte)(((bits >> (bitCount - index - 1)) & 1) << (7 - (bit & 7)));
        }
        return depth;
    }

    internal static TSelf Append(TSelf source, CompressedPrefix prefix, int direction)
    {
        if ((uint)direction > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        int depth = checked(source.BitDepth + prefix.BitCount + 1);
        if (depth > TSelf.MaxBitDepth) throw new ArgumentOutOfRangeException(nameof(prefix));
        Span<byte> path = stackalloc byte[(depth + 7) >> 3];
        path.Clear();
        source.CopyBitsTo(0, path, 0, source.BitDepth);
        PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, path, source.BitDepth);
        if (direction != 0)
        {
            int bit = depth - 1;
            path[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }
        return TSelf.Create(path, depth);
    }

    internal static int Compare<TPath, TOther>(TPath path, TOther other)
        where TPath : struct, IPbtNodePath<TPath>
        where TOther : struct, IPbtNodePath<TOther>
    {
        int depthComparison = path.BitDepth.CompareTo(other.BitDepth);
        if (depthComparison != 0) return depthComparison;
        for (int index = 0; index < (path.BitDepth + 7) >> 3; index++)
        {
            int comparison = path.GetByte(index).CompareTo(other.GetByte(index));
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    internal static bool Equal<TPath, TOther>(TPath path, TOther other)
        where TPath : struct, IPbtNodePath<TPath>
        where TOther : struct, IPbtNodePath<TOther> =>
        path.BitDepth == other.BitDepth && MatchesPrefix(path, other, path.BitDepth);

    internal static bool MatchesPrefix<TPath, TOther>(TPath path, TOther other, int bitCount)
        where TPath : struct, IPbtNodePath<TPath>
        where TOther : struct, IPbtNodePath<TOther>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        if (path.BitDepth < bitCount || other.BitDepth < bitCount) return false;
        int completeBytes = bitCount >> 3;
        for (int index = 0; index < completeBytes; index++)
            if (path.GetByte(index) != other.GetByte(index)) return false;
        int tailBits = bitCount & 7;
        return tailBits == 0 || ((path.GetByte(completeBytes) ^ other.GetByte(completeBytes)) & (0xFF << (8 - tailBits))) == 0;
    }

    internal static int GetBit(ReadOnlySpan<byte> path, int bitDepth, int bitIndex)
    {
        if ((uint)bitIndex >= (uint)bitDepth) throw new ArgumentOutOfRangeException(nameof(bitIndex));
        return (path[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1;
    }

    internal static void CopyBitsTo(ReadOnlySpan<byte> path, int bitDepth, int sourceBitOffset,
        Span<byte> destination, int destinationBitOffset, int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBitOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationBitOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        if (sourceBitOffset > bitDepth - bitCount) throw new ArgumentOutOfRangeException(nameof(bitCount));
        if ((long)destinationBitOffset + bitCount > (long)destination.Length * 8) throw new ArgumentOutOfRangeException(nameof(destinationBitOffset));
        PbtBitPrefix.CopyBits(path, sourceBitOffset, bitCount, destination, destinationBitOffset);
    }

    internal static bool MatchesPrefix(ReadOnlySpan<byte> path, int bitDepth, ReadOnlySpan<byte> key, int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        if (bitCount > bitDepth || (long)bitCount > (long)key.Length * 8) return false;
        int completeBytes = bitCount >> 3;
        int tailBits = bitCount & 7;
        return path[..completeBytes].SequenceEqual(key[..completeBytes])
            && (tailBits == 0 || ((path[completeBytes] ^ key[completeBytes]) & (0xFF << (8 - tailBits))) == 0);
    }

    internal static int Hash(ReadOnlySpan<byte> path, int bitDepth)
    {
        HashCode hash = new();
        hash.Add(bitDepth);
        hash.AddBytes(path);
        return hash.ToHashCode();
    }
}
