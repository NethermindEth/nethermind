// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

internal static class PbtNodePathOperations
{
    [SkipLocalsInit]
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

    [SkipLocalsInit]
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

    [SkipLocalsInit]
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

    /// <summary>Copies a path's canonical bytes into a zeroed destination of sufficient length.</summary>
    internal static void CopyTo<TPath>(TPath path, Span<byte> destination) where TPath : struct, IPbtNodePath<TPath>
    {
        for (int index = 0; index < (path.BitDepth + 7) >> 3; index++) destination[index] = path.GetByte(index);
    }

    internal static int Hash(ReadOnlySpan<byte> path, int bitDepth)
    {
        // The root path equals the default path value, whose cached hash is zero.
        if (path.IsEmpty) return 0;
        HashCode hash = new();
        hash.Add(bitDepth);
        hash.AddBytes(path);
        return hash.ToHashCode();
    }
}
