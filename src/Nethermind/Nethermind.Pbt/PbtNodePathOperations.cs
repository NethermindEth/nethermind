// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;

namespace Nethermind.Pbt;

internal static class PbtNodePathOperations
{
    internal static TPath Prefix<TPath>(ReadOnlySpan<byte> path, int bitDepth, int depth)
        where TPath : struct, IPbtNodePath<TPath>
    {
        if ((uint)depth > (uint)bitDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        Span<byte> prefix = stackalloc byte[(depth + 7) >> 3];
        path[..prefix.Length].CopyTo(prefix);
        if ((depth & 7) != 0) prefix[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        return TPath.Create(prefix, depth);
    }

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
        Debug.Assert(!key.IsEmpty);
        Debug.Assert((uint)bitDepth <= (uint)TPath.MaxBitDepth && bitDepth <= (long)key.Length * 8);
        Span<byte> path = stackalloc byte[(bitDepth + 7) >> 3];
        key[..path.Length].CopyTo(path);
        if (path.Length != 0 && (bitDepth & 7) != 0) path[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        return TPath.Create(path, bitDepth);
    }

    internal static TPath AppendBits<TPath>(ReadOnlySpan<byte> source, int bitDepth, int bits, int bitCount)
        where TPath : struct, IPbtNodePath<TPath>
    {
        if ((uint)bitCount > 4) throw new ArgumentOutOfRangeException(nameof(bitCount));
        if ((uint)bits >= (1u << bitCount)) throw new ArgumentOutOfRangeException(nameof(bits));
        int depth = bitDepth + bitCount;
        if (depth > TPath.MaxBitDepth) throw new ArgumentOutOfRangeException(nameof(bitCount));
        Span<byte> path = stackalloc byte[(depth + 7) >> 3];
        source.CopyTo(path);
        path[source.Length..].Clear();
        if (bitCount != 0)
        {
            int byteIndex = bitDepth >> 3;
            int shiftedBits = bits << (16 - (bitDepth & 7) - bitCount);
            path[byteIndex] |= (byte)(shiftedBits >> 8);
            if (byteIndex + 1 < path.Length) path[byteIndex + 1] = (byte)shiftedBits;
        }
        return TPath.Create(path, depth);
    }

    internal static TPath Append<TPath>(ReadOnlySpan<byte> source, int bitDepth, CompressedPrefix prefix, int direction)
        where TPath : struct, IPbtNodePath<TPath>
    {
        if ((uint)direction > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        int depth = bitDepth + prefix.BitCount + 1;
        if (depth > TPath.MaxBitDepth) throw new ArgumentOutOfRangeException(nameof(prefix));
        Span<byte> path = stackalloc byte[(depth + 7) >> 3];
        source.CopyTo(path);
        path[source.Length..].Clear();
        PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, path, bitDepth);
        if (direction != 0)
        {
            int bit = depth - 1;
            path[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }
        return TPath.Create(path, depth);
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
