// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
