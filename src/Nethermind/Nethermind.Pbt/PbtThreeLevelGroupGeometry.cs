// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Maps canonical node paths to the positions in their three-level node group.</summary>
/// <remarks>
/// A node owns the group whose boundary is at or immediately above its parent. Thus a node at depth
/// three belongs to the root group, while a node at depth four belongs to the group at depth three.
/// The group root is the only exception: the tree root is represented at <see cref="RootPosition"/>.
/// </remarks>
public static class PbtThreeLevelGroupGeometry
{
    /// <summary>The number of levels represented by one group.</summary>
    /// <remarks>
    /// Groups are not byte-aligned: a group's boundary slot can span two key bytes, and a 272-bit key ends one level
    /// before the boundary of its deepest group.
    /// </remarks>
    public const int LevelsPerGroup = 3;

    /// <summary>The number of boundary slots in one group.</summary>
    public const int BoundarySlots = 1 << LevelsPerGroup;

    /// <summary>The number of post-order positions in one group.</summary>
    public const int PositionCount = 2 * BoundarySlots - 1;

    /// <summary>The reserved position of a group's root.</summary>
    public const int RootPosition = PositionCount - 1;

    /// <summary>The maximum path depth supported by the group geometry.</summary>
    public const int MaxPathDepth = PbtVariableTreeKey.MaxLength * 8;

    /// <summary>The greatest depth at which a group key can occur.</summary>
    public const int MaxGroupDepth = MaxPathDepth - LevelsPerGroup;

    /// <summary>Returns whether <paramref name="depth"/> is a valid group depth.</summary>
    public static bool IsGroupDepth(int depth) =>
        (uint)depth <= MaxGroupDepth && depth % LevelsPerGroup == 0;

    /// <summary>Returns the group depth that owns a node at <paramref name="depth"/>.</summary>
    public static int GroupDepthOf(int depth)
    {
        if (depth > MaxPathDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        return depth == 0 ? 0 : (depth - 1) / LevelsPerGroup * LevelsPerGroup;
    }

    public static NodeGroupPath LocalPathOf(int position)
    {
        if ((uint)position >= PositionCount) throw new ArgumentOutOfRangeException(nameof(position));
        int length = PositionDepths[position];
        return new(PositionPaths[position] << (LevelsPerGroup - length), length);
    }

    /// <summary>The position of the node at boundary slot <paramref name="slot"/>.</summary>
    public static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    public static int WidthOf(int position) => BoundarySlots >> PositionDepths[position];

    private static ReadOnlySpan<byte> PositionPaths => [0, 1, 0, 2, 3, 1, 0, 4, 5, 2, 6, 7, 3, 1, 0];

    private static ReadOnlySpan<byte> PositionDepths => [3, 3, 2, 3, 3, 2, 1, 3, 3, 2, 3, 3, 2, 1, 0];
}
