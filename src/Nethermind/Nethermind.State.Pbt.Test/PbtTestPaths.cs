// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>Builds node paths and group locations the tests address nodes by.</summary>
internal static class PbtTestPaths
{
    /// <summary>The first <paramref name="depth"/> bits of <paramref name="bytes"/> as a path.</summary>
    public static TPath Prefix<TPath>(ReadOnlySpan<byte> bytes, int depth) where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> prefix = stackalloc byte[(depth + 7) >> 3];
        bytes[..prefix.Length].CopyTo(prefix);
        if ((depth & 7) != 0) prefix[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        return TPath.Create(prefix, depth);
    }

    /// <summary>The first <paramref name="depth"/> bits of <paramref name="path"/>.</summary>
    public static TPath Prefix<TPath>(this TPath path, int depth) where TPath : struct, IPbtNodePath<TPath>
    {
        Span<byte> bytes = stackalloc byte[(path.BitDepth + 7) >> 3];
        PbtNodePathOperations.CopyTo(path, bytes);
        return Prefix<TPath>(bytes, depth);
    }

    /// <summary>Appends <paramref name="bitCount"/> right-aligned <paramref name="bits"/> to <paramref name="path"/>.</summary>
    public static TPath AppendBits<TPath>(this TPath path, int bits, int bitCount) where TPath : struct, IPbtNodePath<TPath>
    {
        int depth = path.BitDepth + bitCount;
        Span<byte> bytes = stackalloc byte[(depth + 7) >> 3];
        bytes.Clear();
        PbtNodePathOperations.CopyTo(path, bytes);
        for (int bit = 0; bit < bitCount; bit++)
            if (((bits >> (bitCount - 1 - bit)) & 1) != 0) bytes[(path.BitDepth + bit) >> 3] |= (byte)(0x80 >> ((path.BitDepth + bit) & 7));
        return TPath.Create(bytes, depth);
    }

    /// <summary>Appends a compressed prefix and one direction bit to <paramref name="path"/>.</summary>
    public static TPath Append<TPath>(this TPath path, CompressedPrefix prefix, int direction) where TPath : struct, IPbtNodePath<TPath>
    {
        int depth = path.BitDepth + prefix.BitCount + 1;
        Span<byte> bytes = stackalloc byte[(depth + 7) >> 3];
        bytes.Clear();
        PbtNodePathOperations.CopyTo(path, bytes);
        PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, bytes, path.BitDepth);
        if (direction != 0) bytes[(depth - 1) >> 3] |= (byte)(0x80 >> ((depth - 1) & 7));
        return TPath.Create(bytes, depth);
    }

    /// <summary>The group key and position of the node at <paramref name="path"/>.</summary>
    public static PbtNodeGroupLocation<TPath> Locate<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        int groupDepth = PbtFourLevelGroupGeometry.GroupDepthOf(path.BitDepth);
        int relativeDepth = path.BitDepth - groupDepth;
        int position = PbtFourLevelGroupGeometry.RootPosition;
        if (relativeDepth != 0)
        {
            int slot = (path.GetByte(groupDepth >> 3) >> (4 - (groupDepth & 4))) & 0xF;
            position = new NodeGroupPath(slot & ~((PbtFourLevelGroupGeometry.BoundarySlots >> relativeDepth) - 1), relativeDepth).Position;
        }
        return new(path.Prefix(groupDepth), position);
    }

    /// <summary>The path of the node at <paramref name="position"/> in the group at <paramref name="groupKey"/>.</summary>
    public static TPath PathOf<TPath>(TPath groupKey, int position) where TPath : struct, IPbtNodePath<TPath>
    {
        NodeGroupPath local = PbtFourLevelGroupGeometry.LocalPathOf(position);
        return groupKey.AppendBits(local.Slot >> (PbtFourLevelGroupGeometry.LevelsPerGroup - local.Length), local.Length);
    }
}

/// <summary>Identifies one node's group key and post-order position.</summary>
internal readonly record struct PbtNodeGroupLocation<TPath>(TPath GroupKey, int Position) where TPath : struct, IPbtNodePath<TPath>;
