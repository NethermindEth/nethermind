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
    public const int MaxPathDepth = PbtFullKey.MaxLength * 8;

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
    public static PbtNodeGroupLocation Locate(PbtNodePath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int groupDepth = GroupDepthOf(path.BitDepth);
        PbtNodePath groupKey = Prefix(path, groupDepth);
        int position = PositionOf(path, groupDepth);
        return new PbtNodeGroupLocation(groupKey, position);
    }

    /// <summary>Returns the group key owning <paramref name="path"/>.</summary>
    public static PbtNodePath GroupKeyOf(PbtNodePath path) => Locate(path).GroupKey;

    /// <summary>Returns the position of <paramref name="path"/> in its owning group.</summary>
    public static int PositionOf(PbtNodePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PositionOf(path, GroupDepthOf(path.BitDepth));
    }

    /// <summary>Reconstructs a canonical path from a group key and one of its positions.</summary>
    public static PbtNodePath PathOf(PbtNodePath groupKey, int position)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ValidateGroupKey(groupKey);
        ValidatePosition(groupKey, position);

        if (position == RootPosition) return new PbtNodePath([], 0);

        int currentPosition = RootPosition;
        int width = BoundarySlots;
        Span<byte> directions = stackalloc byte[LevelsPerGroup];
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
                directions[relativeDepth++] = 0;
                currentPosition = leftPosition;
                width = halfWidth;
            }
            else if (position >= rightFirst && position <= rightPosition)
            {
                directions[relativeDepth++] = 1;
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

        int depth = checked(groupKey.BitDepth + relativeDepth);
        byte[] path = new byte[(depth + 7) >> 3];
        groupKey.Path.CopyTo(path);
        for (int index = 0; index < relativeDepth; index++)
        {
            if (directions[index] == 0) continue;
            int bit = groupKey.BitDepth + index;
            path[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }

        return new PbtNodePath(path, depth);
    }

    /// <summary>Reconstructs a canonical path from a group key and one of its positions.</summary>
    public static PbtNodePath Reconstruct(PbtNodePath groupKey, int position) => PathOf(groupKey, position);

    private static int PositionOf(PbtNodePath path, int groupDepth)
    {
        int relativeDepth = path.BitDepth - groupDepth;
        int position = RootPosition;
        int width = BoundarySlots;
        for (int index = 0; index < relativeDepth; index++)
        {
            int bitIndex = groupDepth + index;
            int direction = (path.Path[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1;
            position = direction == 0 ? position - width : position - 1;
            width /= 2;
        }

        return position;
    }

    private static PbtNodePath Prefix(PbtNodePath path, int depth)
    {
        int byteLength = (depth + 7) >> 3;
        byte[] prefix = path.Path[..byteLength].ToArray();
        if (byteLength != 0 && (depth & 7) != 0)
            prefix[^1] &= (byte)(0xFF << (8 - (depth & 7)));
        return new PbtNodePath(prefix, depth);
    }

    private static void ValidateGroupKey(PbtNodePath groupKey)
    {
        if (!IsGroupDepth(groupKey.BitDepth))
        {
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        }
    }

    private static void ValidatePosition(PbtNodePath groupKey, int position)
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
public readonly record struct PbtNodeGroupLocation(PbtNodePath GroupKey, int Position);
