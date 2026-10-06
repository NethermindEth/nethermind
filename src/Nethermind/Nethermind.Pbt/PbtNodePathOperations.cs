// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Pbt;

internal static class PbtNodePathOperations
{
    [SkipLocalsInit]
    internal static TPath Prefix<TPath>(ReadOnlySpan<byte> path, int bitDepth, int depth)
        where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> prefix = stackalloc byte[(depth + 7) >> 3];
        path[..prefix.Length].CopyTo(prefix);
        if ((depth & 7) != 0) prefix[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        return TPath.Create(prefix, depth);
    }

    [SkipLocalsInit]
    internal static TPath AppendBits<TPath>(ReadOnlySpan<byte> source, int bitDepth, int bits, int bitCount)
        where TPath : struct, IPbtNodePath<TPath>
    {
        int depth = bitDepth + bitCount;
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
        int depth = bitDepth + prefix.BitCount + 1;
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
        where TOther : struct, IPbtNodePath<TOther>
    {
        if (path.BitDepth != other.BitDepth) return false;
        for (int index = 0; index < (path.BitDepth + 7) >> 3; index++)
            if (path.GetByte(index) != other.GetByte(index)) return false;
        return true;
    }

    /// <summary>Copies a path's canonical bytes into a zeroed destination of sufficient length.</summary>
    internal static void CopyTo<TPath>(TPath path, Span<byte> destination) where TPath : struct, IPbtNodePath<TPath>
    {
        for (int index = 0; index < (path.BitDepth + 7) >> 3; index++) destination[index] = path.GetByte(index);
    }

    internal static int Hash(ReadOnlySpan<byte> path, int bitDepth) =>
        // The root path equals the default path value, whose cached hash is zero.
        path.IsEmpty ? 0 : HashCode.Combine(bitDepth, path.FastHash());
}
