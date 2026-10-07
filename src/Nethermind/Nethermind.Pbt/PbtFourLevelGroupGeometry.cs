// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Maps canonical node paths to the positions in their four-level node group.</summary>
/// <remarks>
/// A node owns the group whose boundary is at or immediately above its parent. Thus a node at depth
/// four belongs to the root group, while a node at depth five belongs to the group at depth four.
/// The group root is the only exception: the tree root is represented at <see cref="RootPosition"/>.
/// </remarks>
public static class PbtFourLevelGroupGeometry
{
    /// <summary>The number of levels represented by one group.</summary>
    /// <remarks>
    /// Lowering it to three increases CPU use, while raising it above four increases the compaction and snapshot memory
    /// overhead, which ends up slower in full sync.
    /// </remarks>
    public const int LevelsPerGroup = 4;

    /// <summary>The number of boundary slots in one group.</summary>
    public const int BoundarySlots = 1 << LevelsPerGroup;

    /// <summary>The number of post-order positions in one group.</summary>
    public const int PositionCount = 2 * BoundarySlots - 1;

    /// <summary>The reserved position of a group's root.</summary>
    public const int RootPosition = PositionCount - 1;

    /// <summary>The maximum path depth supported by the four-level group geometry.</summary>
    public const int MaxPathDepth = PbtStorageTreeKey.MaxLength * 8;

    /// <summary>The greatest depth at which a group key can occur.</summary>
    public const int MaxGroupDepth = MaxPathDepth - LevelsPerGroup;

    /// <summary>Returns whether <paramref name="depth"/> is a valid four-level group depth.</summary>
    public static bool IsGroupDepth(int depth) =>
        (uint)depth <= MaxGroupDepth && depth % LevelsPerGroup == 0;

    /// <summary>Returns the group depth that owns a node at <paramref name="depth"/>.</summary>
    public static int GroupDepthOf(int depth)
    {
        if (depth > MaxPathDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        return depth == 0 ? 0 : (depth - 1) / LevelsPerGroup * LevelsPerGroup;
    }

    internal static NodeGroupPath LocalPathOf(int position)
    {
        if ((uint)position >= PositionCount) throw new ArgumentOutOfRangeException(nameof(position));
        int length = PositionDepths[position];
        return new(PositionNibbles[position] << (4 - length), length);
    }

    /// <summary>The position of the node at boundary slot <paramref name="slot"/>.</summary>
    internal static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    internal static int WidthOf(int position) => BoundarySlots >> PositionDepths[position];

    private static ReadOnlySpan<byte> PositionNibbles => [0, 1, 0, 2, 3, 1, 0, 4, 5, 2, 6, 7, 3, 1, 0, 8, 9, 4, 10, 11, 5, 2, 12, 13, 6, 14, 15, 7, 3, 1, 0];

    private static ReadOnlySpan<byte> PositionDepths => [4, 4, 3, 4, 4, 3, 2, 4, 4, 3, 4, 4, 3, 2, 1, 4, 4, 3, 4, 4, 3, 2, 4, 4, 3, 4, 4, 3, 2, 1, 0];
}
