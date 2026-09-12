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
    public const int LevelsPerGroup = 4;

    /// <summary>The number of boundary slots in one group.</summary>
    public const int BoundarySlots = 1 << LevelsPerGroup;

    /// <summary>The number of post-order positions in one group.</summary>
    public const int PositionCount = 2 * BoundarySlots - 1;

    /// <summary>The reserved position of a group's root.</summary>
    public const int RootPosition = PositionCount - 1;

    /// <summary>The maximum path depth supported by the four-level group geometry.</summary>
    public const int MaxPathDepth = PbtStorageFullKey.MaxLength * 8;

    /// <summary>The greatest depth at which a group key can occur.</summary>
    public const int MaxGroupDepth = MaxPathDepth - LevelsPerGroup;

    /// <summary>Returns whether <paramref name="depth"/> is a valid four-level group depth.</summary>
    public static bool IsGroupDepth(int depth) =>
        (uint)depth <= MaxGroupDepth && depth % LevelsPerGroup == 0;

    /// <summary>Returns the group depth that owns a node at <paramref name="depth"/>.</summary>
    public static int GroupDepthOf(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        if (depth > MaxPathDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        return depth == 0 ? 0 : (depth - 1) / LevelsPerGroup * LevelsPerGroup;
    }

    /// <summary>Returns the group key and position for <paramref name="path"/>.</summary>
    public static PbtNodeGroupLocation<TPath> Locate<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        int groupDepth = GroupDepthOf(path.BitDepth);
        TPath groupKey = path.Prefix(groupDepth);
        int position = PositionOf(path, groupDepth);
        return new PbtNodeGroupLocation<TPath>(groupKey, position);
    }

    /// <summary>Returns the group key owning <paramref name="path"/>.</summary>
    public static TPath GroupKeyOf<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath> => path.Prefix(GroupDepthOf(path.BitDepth));

    /// <summary>Returns the position of <paramref name="path"/> in its owning group.</summary>
    public static int PositionOf<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath> => PositionOf(path, GroupDepthOf(path.BitDepth));

    /// <summary>Reconstructs a canonical path from a group key and one of its positions.</summary>
    public static TPath PathOf<TPath>(TPath groupKey, int position) where TPath : struct, IPbtNodePath<TPath>
    {
        ValidateGroupKey(groupKey);
        ValidatePosition(groupKey, position);

        if (position == RootPosition) return TPath.Create([], 0);

        int nibble = PositionNibbles[position];
        int relativeDepth = PositionDepths[position];

        if (relativeDepth == LevelsPerGroup) return groupKey.AppendNib(nibble);

        return groupKey.AppendBits(nibble, relativeDepth);
    }

    internal static NodeGroupPath LocalPathOf(int position)
    {
        if ((uint)position >= PositionCount) throw new ArgumentOutOfRangeException(nameof(position));
        int length = PositionDepths[position];
        return new(PositionNibbles[position] << (4 - length), length);
    }

    /// <summary>Reconstructs a canonical path from a group key and one of its positions.</summary>
    public static TPath Reconstruct<TPath>(TPath groupKey, int position) where TPath : struct, IPbtNodePath<TPath> => PathOf(groupKey, position);

    internal static int WidthOf(int position) => position switch
    {
        2 or 5 or 9 or 12 or 17 or 20 or 24 or 27 => 2,
        6 or 13 or 21 or 28 => 4,
        14 or 29 => 8,
        RootPosition => BoundarySlots,
        _ => 1
    };

    private static ReadOnlySpan<byte> PositionNibbles => [0, 1, 0, 2, 3, 1, 0, 4, 5, 2, 6, 7, 3, 1, 0, 8, 9, 4, 10, 11, 5, 2, 12, 13, 6, 14, 15, 7, 3, 1, 0];

    private static ReadOnlySpan<byte> PositionDepths => [4, 4, 3, 4, 4, 3, 2, 4, 4, 3, 4, 4, 3, 2, 1, 4, 4, 3, 4, 4, 3, 2, 4, 4, 3, 4, 4, 3, 2, 1, 0];

    private static int PositionOf<TPath>(TPath path, int groupDepth) where TPath : struct, IPbtNodePath<TPath>
    {
        int relativeDepth = path.BitDepth - groupDepth;
        if (relativeDepth == 0) return RootPosition;
        int slot = (path.GetByte(groupDepth >> 3) >> (4 - (groupDepth & 4))) & 0xF;
        int width = BoundarySlots >> relativeDepth;
        slot &= ~(width - 1);
        return 2 * (slot + width) - 2 - BitOperations.PopCount((uint)slot);
    }

    private static void ValidateGroupKey<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!IsGroupDepth(groupKey.BitDepth))
        {
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        }
    }

    private static void ValidatePosition<TPath>(TPath groupKey, int position) where TPath : struct, IPbtNodePath<TPath>
    {
        if ((uint)position >= PositionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (position == RootPosition && groupKey.BitDepth != 0)
        {
            throw new ArgumentException("The reserved root position is valid only in the root group.", nameof(position));
        }
    }
}

/// <summary>Identifies one node's group key and post-order position.</summary>
public readonly record struct PbtNodeGroupLocation<TPath>(TPath GroupKey, int Position) where TPath : struct, IPbtNodePath<TPath>;
