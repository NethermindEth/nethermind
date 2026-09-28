// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>Maps canonical node paths to the positions in their node group.</summary>
/// <remarks>
/// A node owns the group whose boundary is at or immediately above its parent. Thus a node at depth
/// <see cref="LevelsPerGroup"/> belongs to the root group, while the node one level deeper belongs to the group at that depth.
/// The group root is the only exception: the tree root is represented at <see cref="RootPosition"/>.
/// <para/>
/// The number of levels per group is fixed for the process when this type is first used: it is read from the
/// <see cref="LevelsPerGroupDataName"/> <see cref="AppContext"/> data, then the <c>NETHERMIND_PBT_LEVELS_PER_GROUP</c>
/// environment variable, and defaults to four. Being <see langword="static"/> <see langword="readonly"/>, the JIT folds it.
/// </remarks>
public static class PbtGroupGeometry
{
    /// <summary>The <see cref="AppContext"/> data name <see cref="Configure"/> sets the levels per group through.</summary>
    public const string LevelsPerGroupDataName = "Nethermind.Pbt.LevelsPerGroup";
    private const string LevelsPerGroupEnvironmentVariable = "NETHERMIND_PBT_LEVELS_PER_GROUP";
    private const int DefaultLevelsPerGroup = 4;

    /// <summary>The largest supported number of levels per group.</summary>
    public const int MaxLevelsPerGroup = 8;

    /// <summary>The number of boundary slots of the widest supported group.</summary>
    public const int MaxBoundarySlots = 1 << MaxLevelsPerGroup;

    /// <summary>The number of post-order positions of the widest supported group.</summary>
    public const int MaxPositionCount = 2 * MaxBoundarySlots - 1;

    /// <summary>The number of positions per-group buffers hold inline; wider groups rent them.</summary>
    internal const int InlinePositionCapacity = 63;

    /// <summary>The number of levels represented by one group.</summary>
    public static readonly int LevelsPerGroup = ReadLevelsPerGroup();

    /// <summary>The number of boundary slots in one group.</summary>
    public static readonly int BoundarySlots = 1 << LevelsPerGroup;

    /// <summary>The number of post-order positions in one group.</summary>
    public static readonly int PositionCount = 2 * BoundarySlots - 1;

    /// <summary>The reserved position of a group's root.</summary>
    public static readonly int RootPosition = PositionCount - 1;

    /// <summary>The maximum path depth supported by the group geometry.</summary>
    public const int MaxPathDepth = PbtStorageTreeKey.MaxLength * 8;

    /// <summary>The greatest depth at which a group key can occur.</summary>
    public static readonly int MaxGroupDepth = (MaxPathDepth - 1) / LevelsPerGroup * LevelsPerGroup;

    /// <summary>The shallowest group depth below the zone byte, where each zone's groups start.</summary>
    internal static readonly int ZoneGroupDepth = (8 + LevelsPerGroup - 1) / LevelsPerGroup * LevelsPerGroup;

    /// <summary>The key bits between the zone byte and <see cref="ZoneGroupDepth"/>.</summary>
    internal static readonly int UnitBits = ZoneGroupDepth - 8;

    private static readonly (NodeGroupPath[] LocalPaths, short[] Parents) Layout = CreateLayout(LevelsPerGroup);

    /// <summary>Fixes the levels per group of this process, before the geometry is first used.</summary>
    /// <exception cref="InvalidOperationException">The geometry is already in use with other levels per group.</exception>
    public static void Configure(int levelsPerGroup)
    {
        ValidateLevelsPerGroup(levelsPerGroup);
        AppContext.SetData(LevelsPerGroupDataName, levelsPerGroup);
        int configured = ConfiguredLevelsPerGroup();
        if (configured != levelsPerGroup)
            throw new InvalidOperationException($"The PBT group geometry is already in use with {configured} levels per group, so it cannot be configured with {levelsPerGroup}.");
    }

    // Kept out of Configure, so compiling it cannot initialize the geometry before the data is set.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ConfiguredLevelsPerGroup() => LevelsPerGroup;

    private static int ReadLevelsPerGroup()
    {
        object? data = AppContext.GetData(LevelsPerGroupDataName);
        string? environment = Environment.GetEnvironmentVariable(LevelsPerGroupEnvironmentVariable);
        int levelsPerGroup = data switch
        {
            int value => value,
            string value => int.Parse(value, CultureInfo.InvariantCulture),
            _ => string.IsNullOrEmpty(environment) ? DefaultLevelsPerGroup : int.Parse(environment, CultureInfo.InvariantCulture),
        };
        ValidateLevelsPerGroup(levelsPerGroup);
        return levelsPerGroup;
    }

    private static void ValidateLevelsPerGroup(int levelsPerGroup)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levelsPerGroup, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levelsPerGroup, MaxLevelsPerGroup);
    }

    /// <summary>Builds the local path and parent position of every position of a group with <paramref name="levelsPerGroup"/> levels.</summary>
    internal static (NodeGroupPath[] LocalPaths, short[] Parents) CreateLayout(int levelsPerGroup)
    {
        int slots = 1 << levelsPerGroup;
        NodeGroupPath[] localPaths = new NodeGroupPath[2 * slots - 1];
        short[] parents = new short[localPaths.Length];
        for (int length = 0; length <= levelsPerGroup; length++)
        {
            int width = slots >> length;
            for (int slot = 0; slot < slots; slot += width)
            {
                int position = NodeGroupPath.PositionOf(slot, width);
                localPaths[position] = NodeGroupPath.FromValidated(slot, length);
                parents[position] = length == 0 ? (short)-1 : (short)NodeGroupPath.PositionOf(slot & ~(2 * width - 1), 2 * width);
            }
        }
        return (localPaths, parents);
    }

    /// <summary>Returns whether <paramref name="depth"/> is a valid group depth.</summary>
    public static bool IsGroupDepth(int depth) =>
        (uint)depth <= MaxGroupDepth && depth % LevelsPerGroup == 0;

    /// <summary>Returns the group depth that owns a node at <paramref name="depth"/>.</summary>
    public static int GroupDepthOf(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        if (depth > MaxPathDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        return depth == 0 ? 0 : (depth - 1) / LevelsPerGroup * LevelsPerGroup;
    }

    /// <summary>Reads the <see cref="LevelsPerGroup"/> key bits at <paramref name="bitOffset"/>, the boundary slot of the group there.</summary>
    /// <remarks>
    /// Bits past the end of <paramref name="bytes"/> read as zero, since a key's length need not be a multiple of the levels per group.
    /// When the levels divide eight, <paramref name="bitOffset"/> must be a multiple of them, as a group depth is, and so is one less whole bytes.
    /// </remarks>
    internal static int ReadSlot(ReadOnlySpan<byte> bytes, int bitOffset)
    {
        int index = bitOffset >> 3;
        // A group within a byte never straddles one when its levels divide eight.
        if (8 % LevelsPerGroup == 0) return (bytes[index] >> (8 - LevelsPerGroup - (bitOffset & 7))) & (BoundarySlots - 1);
        int window = bytes[index] << 8;
        if ((uint)(index + 1) < (uint)bytes.Length) window |= bytes[index + 1];
        return (window >> (16 - LevelsPerGroup - (bitOffset & 7))) & (BoundarySlots - 1);
    }

    /// <summary>Returns the group key and position for <paramref name="path"/>.</summary>
    public static PbtNodeGroupLocation<TPath> Locate<TPath>(TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        int groupDepth = GroupDepthOf(path.BitDepth);
        TPath groupKey = path.Prefix(groupDepth);
        int position = PositionOf(path, groupDepth);
        return new PbtNodeGroupLocation<TPath>(groupKey, position);
    }

    /// <summary>Reconstructs a canonical path from a group key and one of its positions.</summary>
    public static TPath PathOf<TPath>(TPath groupKey, int position) where TPath : struct, IPbtNodePath<TPath>
    {
        ValidateGroupKey(groupKey);
        ValidatePosition(groupKey, position);

        if (position == RootPosition) return TPath.Create([], 0);

        NodeGroupPath local = LocalPathOf(position);
        return groupKey.AppendBits(local.Slot >> (LevelsPerGroup - local.Length), local.Length);
    }

    internal static NodeGroupPath LocalPathOf(int position) => Layout.LocalPaths[position];

    /// <summary>The position holding the node one level above <paramref name="position"/>, or -1 for the group root.</summary>
    internal static int ParentOf(int position) => Layout.Parents[position];

    internal static int WidthOf(int position) => LocalPathOf(position).Width;

    private static int PositionOf<TPath>(TPath path, int groupDepth) where TPath : struct, IPbtNodePath<TPath>
    {
        int relativeDepth = path.BitDepth - groupDepth;
        if (relativeDepth == 0) return RootPosition;
        int index = groupDepth >> 3;
        int window = path.GetByte(index) << 8;
        if (index + 1 < PbtBitPrefix.ByteCount(path.BitDepth)) window |= path.GetByte(index + 1);
        int slot = (window >> (16 - LevelsPerGroup - (groupDepth & 7))) & (BoundarySlots - 1);
        int width = BoundarySlots >> relativeDepth;
        return NodeGroupPath.PositionOf(slot & ~(width - 1), width);
    }

    private static void ValidateGroupKey<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!IsGroupDepth(groupKey.BitDepth))
        {
            throw new ArgumentException("A group key depth must be a group boundary.", nameof(groupKey));
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
