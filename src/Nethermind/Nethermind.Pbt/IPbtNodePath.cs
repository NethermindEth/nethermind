// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>A canonical node's structural identity, independent of its in-memory capacity.</summary>
public interface IPbtNodePath : IEquatable<IPbtNodePath>, IComparable<IPbtNodePath>
{
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
    /// <summary>Returns a path with four bits appended, selecting the smallest supported capacity.</summary>
    /// <param name="nibble">A value from zero to fifteen, appended most significant bit first.</param>
    /// <exception cref="ArgumentOutOfRangeException">The nibble is outside zero to fifteen, or the resulting path exceeds the maximum capacity.</exception>
    IPbtNodePath AppendNib(int nibble);
    /// <summary>Appends zero to four right-aligned bits, selecting the smallest supported capacity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count, bits, or resulting depth is outside its supported range.</exception>
    IPbtNodePath AppendBits(int bits, int bitCount);
    /// <summary>Appends zero to four right-aligned bits into the selected path capacity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count, bits, or resulting depth is outside its supported range.</exception>
    TPath AppendBits<TPath>(int bits, int bitCount) where TPath : struct, IPbtNodePath<TPath>;
    /// <summary>Converts this path to the selected capacity without changing its identity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The path exceeds the selected capacity.</exception>
    TPath ToPath<TPath>() where TPath : struct, IPbtNodePath<TPath>;
    /// <summary>Tests the first bits against another path; insufficient length is a mismatch.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bit count is negative.</exception>
    bool MatchesPrefix<TOther>(TOther other, int bitCount) where TOther : IPbtNodePath;
    /// <summary>Gets the encoded length of the four-byte depth and canonical path bytes.</summary>
    int EncodedLength { get; }
    /// <summary>Writes the big-endian depth and canonical path bytes, leaving excess destination bytes unchanged.</summary>
    /// <exception cref="ArgumentException">The destination is shorter than <see cref="EncodedLength"/>.</exception>
    void Write(Span<byte> destination);
}

/// <summary>Constructs paths with a statically selected inline capacity.</summary>
public interface IPbtNodePath<TSelf> : IPbtNodePath where TSelf : struct, IPbtNodePath<TSelf>
{
    /// <summary>Gets the maximum supported path depth.</summary>
    static abstract int MaxBitDepth { get; }
    /// <summary>Creates a path, validating its length and unused bits.</summary>
    static abstract TSelf Create(ReadOnlySpan<byte> path, int bitDepth);
}
