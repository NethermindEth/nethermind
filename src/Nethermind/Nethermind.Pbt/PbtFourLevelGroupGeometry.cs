// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public static PbtNodeGroupLocation Locate(IPbtNodePath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int groupDepth = GroupDepthOf(path.BitDepth);
        IPbtNodePath groupKey = Prefix(path, groupDepth);
        int position = PositionOf(path, groupDepth);
        return new PbtNodeGroupLocation(groupKey, position);
    }

    /// <summary>Returns the group key owning <paramref name="path"/>.</summary>
    public static IPbtNodePath GroupKeyOf(IPbtNodePath path) => Locate(path).GroupKey;

    /// <summary>Returns the position of <paramref name="path"/> in its owning group.</summary>
    public static int PositionOf(IPbtNodePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PositionOf(path, GroupDepthOf(path.BitDepth));
    }

    /// <summary>Reconstructs a canonical path from a group key and one of its positions.</summary>
    public static IPbtNodePath PathOf(IPbtNodePath groupKey, int position)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ValidateGroupKey(groupKey);
        ValidatePosition(groupKey, position);

        if (position == RootPosition) return PbtPathOperations.Create([], 0);

        int currentPosition = RootPosition;
        int width = BoundarySlots;
        int nibble = 0;
        int relativeDepth = 0;
        while (relativeDepth < LevelsPerGroup)
        {
            if (position == currentPosition) break;

            int halfWidth = width / 2;
            int leftPosition = currentPosition - width;
            int rightPosition = currentPosition - 1;
            int leftFirst = leftPosition - 2 * halfWidth + 2;
            int rightFirst = rightPosition - 2 * halfWidth + 2;
            if (position >= leftFirst && position <= leftPosition)
            {
                nibble <<= 1;
                relativeDepth++;
                currentPosition = leftPosition;
                width = halfWidth;
            }
            else if (position >= rightFirst && position <= rightPosition)
            {
                nibble = (nibble << 1) | 1;
                relativeDepth++;
                currentPosition = rightPosition;
                width = halfWidth;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(position), "Position is not in the four-level post-order tree.");
            }
        }

        if (position != currentPosition || relativeDepth == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Position is not in the four-level post-order tree.");
        }

        if (relativeDepth == LevelsPerGroup) return groupKey.AppendNib(nibble);

        return groupKey.AppendBits(nibble, relativeDepth);
    }

    /// <summary>Reconstructs a canonical path from a group key and one of its positions.</summary>
    public static IPbtNodePath Reconstruct(IPbtNodePath groupKey, int position) => PathOf(groupKey, position);

    internal static int WidthOf(int position) => position switch
    {
        2 or 5 or 9 or 12 or 17 or 20 or 24 or 27 => 2,
        6 or 13 or 21 or 28 => 4,
        14 or 29 => 8,
        RootPosition => BoundarySlots,
        _ => 1
    };

    private static int PositionOf(IPbtNodePath path, int groupDepth)
    {
        int relativeDepth = path.BitDepth - groupDepth;
        int position = RootPosition;
        int width = BoundarySlots;
        for (int index = 0; index < relativeDepth; index++)
        {
            int bitIndex = groupDepth + index;
            int direction = path.GetBit(bitIndex);
            position = direction == 0 ? position - width : position - 1;
            width /= 2;
        }

        return position;
    }

    private static IPbtNodePath Prefix(IPbtNodePath path, int depth)
    {
        int byteLength = (depth + 7) >> 3;
        Span<byte> prefix = stackalloc byte[byteLength];
        prefix.Clear();
        path.CopyBitsTo(0, prefix, 0, depth);
        return PbtPathOperations.Create(prefix, depth);
    }

    private static void ValidateGroupKey(IPbtNodePath groupKey)
    {
        if (!IsGroupDepth(groupKey.BitDepth))
        {
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        }
    }

    private static void ValidatePosition(IPbtNodePath groupKey, int position)
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
public readonly record struct PbtNodeGroupLocation(IPbtNodePath GroupKey, int Position);
