// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Pbt;

public static class PbtNodePathOperations
{
    public static bool Equal<TPath, TOther>(TPath path, TOther other)
        where TPath : struct, IPbtNodePath<TPath>
        where TOther : struct, IPbtNodePath<TOther>
    {
        if (path.BitDepth != other.BitDepth) return false;
        for (int index = 0; index < (path.BitDepth + 7) >> 3; index++)
            if (path.GetByte(index) != other.GetByte(index)) return false;
        return true;
    }

    /// <summary>Copies a path's canonical bytes into a zeroed destination of sufficient length.</summary>
    public static void CopyTo<TPath>(TPath path, Span<byte> destination) where TPath : struct, IPbtNodePath<TPath>
    {
        for (int index = 0; index < (path.BitDepth + 7) >> 3; index++) destination[index] = path.GetByte(index);
    }

    public static int Hash(ReadOnlySpan<byte> path, int bitDepth) =>
        // The root path equals the default path value, whose cached hash is zero.
        path.IsEmpty ? 0 : HashCode.Combine(bitDepth, path.FastHash());
}
