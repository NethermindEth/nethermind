// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Service;

namespace Nethermind.StateComposition.Diff;

/// <summary>
/// Fixed slot index for the nine per-depth counter rows in
/// <see cref="CumulativeDepthStats.ByDepth"/>. The numeric order is load-bearing:
/// it pins the RLP layout that the snapshot decoder writes and reads back.
/// </summary>
public enum DepthSlot
{
    AccountFull = 0,
    AccountShort = 1,
    AccountValue = 2,
    AccountBytes = 3,
    StorageFull = 4,
    StorageShort = 5,
    StorageValue = 6,
    StorageBytes = 7,
    BranchOccupancy = 8,
}

/// <summary>
/// Mutable cumulative per-depth trie node counters. Doubles as the per-block
/// delta container produced by <see cref="TrieDiffWalker"/> — same shape, same
/// field semantics, merged into the holder's baseline via <see cref="AddInPlace"/>.
///
/// Storage layout: a single <c>long[9][16]</c> jagged array indexed by
/// <see cref="DepthSlot"/>. Every cumulative operation (Reset, Clone, AddInPlace,
/// IsEmpty, SeedFromSnapshot) is one loop instead of nine field-by-field copies.
///
/// Physical-depth storage: rows are indexed by physical depth [0..15].
/// The Geth +1 shift for ValueNodeCount is applied only at presentation time
/// in <see cref="Metrics.UpdateDepthDistribution"/> — never stored here.
///
/// AccountShortNodes[d] = extensions + leaves at physical depth d (matches Geth ShortNode convention).
/// AccountValueNodes[d] = leaves at physical depth d (unshifted).
/// </summary>
public sealed class CumulativeDepthStats
{
    internal const int SlotCount = 9;
    internal const int DepthCount = 16;

    /// <summary>Nine contiguous <c>long[16]</c> rows, indexed by <see cref="DepthSlot"/>.</summary>
    public long[][] ByDepth { get; }

    public CumulativeDepthStats()
    {
        ByDepth = new long[SlotCount][];
        for (int s = 0; s < SlotCount; s++) ByDepth[s] = new long[DepthCount];
    }

    public long[] AccountFullNodes => ByDepth[(int)DepthSlot.AccountFull];
    public long[] AccountShortNodes => ByDepth[(int)DepthSlot.AccountShort];
    public long[] AccountValueNodes => ByDepth[(int)DepthSlot.AccountValue];
    public long[] AccountNodeBytes => ByDepth[(int)DepthSlot.AccountBytes];
    public long[] StorageFullNodes => ByDepth[(int)DepthSlot.StorageFull];
    public long[] StorageShortNodes => ByDepth[(int)DepthSlot.StorageShort];
    public long[] StorageValueNodes => ByDepth[(int)DepthSlot.StorageValue];
    public long[] StorageNodeBytes => ByDepth[(int)DepthSlot.StorageBytes];
    /// <summary>Branch occupancy histogram: index i = count of account-trie branches with (i+1) children.</summary>
    public long[] BranchOccupancy => ByDepth[(int)DepthSlot.BranchOccupancy];

    public long TotalBranchNodes { get; set; }
    public long TotalBranchChildren { get; set; }

    /// <summary>
    /// True once a baseline has been installed via <see cref="SeedFromScan"/> or
    /// <see cref="SeedFromSnapshot"/>. Until that happens, <see cref="AddInPlace"/>
    /// is a no-op — applying deltas on top of an unseeded (all-zero) baseline would
    /// push gauges negative whenever a diff removes more nodes than it adds at a
    /// given depth. The gate guarantees gauges stay at 0 until a correct baseline is
    /// available, eliminating the "negative metrics" failure mode across restarts
    /// and cold starts.
    /// </summary>
    public bool IsSeeded { get; private set; }

    /// <summary>
    /// Add another instance's fields into this one in place. Values in
    /// <paramref name="other"/> can be negative (diff removes). Allocation-free on the hot path.
    /// </summary>
    /// <remarks>No-op until this instance's <see cref="IsSeeded"/> is true.</remarks>
    public void AddInPlace(CumulativeDepthStats other)
    {
        if (!IsSeeded) return;
        for (int s = 0; s < SlotCount; s++)
        {
            long[] dst = ByDepth[s];
            long[] src = other.ByDepth[s];
            for (int d = 0; d < DepthCount; d++) dst[d] += src[d];
        }
        TotalBranchNodes += other.TotalBranchNodes;
        TotalBranchChildren += other.TotalBranchChildren;
    }

    /// <summary>
    /// Returns true when every slot row and both scalars are zero.
    /// Used by the diff walker to skip <see cref="Metrics.UpdateDepthDistribution"/>
    /// republishes for blocks that do not change the depth distribution.
    /// </summary>
    public bool IsEmpty()
    {
        if (TotalBranchNodes != 0 || TotalBranchChildren != 0) return false;
        for (int s = 0; s < SlotCount; s++)
        {
            long[] row = ByDepth[s];
            for (int d = 0; d < DepthCount; d++)
                if (row[d] != 0) return false;
        }
        return true;
    }

    /// <summary>Zero all rows and scalars. Used when a fresh scan re-baselines.</summary>
    public void Reset()
    {
        for (int s = 0; s < SlotCount; s++) Array.Clear(ByDepth[s]);
        TotalBranchNodes = 0;
        TotalBranchChildren = 0;
        IsSeeded = false;
    }

    /// <summary>
    /// Populate from a completed full scan's <see cref="TrieDepthDistribution"/>.
    ///
    /// Conversion rules (reversing BuildLevelStats in StateCompositionVisitor):
    ///   stat.FullNodeCount  at depth d → AccountFullNodes[d]
    ///   stat.ShortNodeCount at depth d → AccountShortNodes[d]  (extensions+leaves at depth d)
    ///   stat.ValueNodeCount at depth d → AccountValueNodes[d-1] (shift reversed: physical depth = d-1)
    ///   stat.TotalSize      at depth d → AccountNodeBytes[d]
    ///
    /// Branch occupancy and totals come from BranchOccupancyDistribution and scalars.
    /// </summary>
    public void SeedFromScan(TrieDepthDistribution dist)
    {
        Reset();

        foreach (TrieLevelStat stat in dist.AccountTrieLevels)
        {
            int d = stat.Depth < DepthCount ? stat.Depth : DepthCount - 1;
            AccountFullNodes[d] = stat.FullNodeCount;
            AccountShortNodes[d] = stat.ShortNodeCount;
            AccountNodeBytes[d] = stat.TotalSize;
            if (d > 0) AccountValueNodes[d - 1] = stat.ValueNodeCount;
        }

        foreach (TrieLevelStat stat in dist.StorageTrieLevels)
        {
            int d = stat.Depth < DepthCount ? stat.Depth : DepthCount - 1;
            StorageFullNodes[d] = stat.FullNodeCount;
            StorageShortNodes[d] = stat.ShortNodeCount;
            StorageNodeBytes[d] = stat.TotalSize;
            if (d > 0) StorageValueNodes[d - 1] = stat.ValueNodeCount;
        }

        for (int i = 0; i < dist.BranchOccupancyDistribution.Length && i < DepthCount; i++)
            BranchOccupancy[i] = dist.BranchOccupancyDistribution[i];

        long nodes = 0, children = 0;
        for (int i = 0; i < DepthCount; i++)
        {
            nodes += BranchOccupancy[i];
            children += BranchOccupancy[i] * (i + 1);
        }
        TotalBranchNodes = nodes;
        TotalBranchChildren = children;
        IsSeeded = true;
    }

    /// <summary>
    /// Directly flip the IsSeeded flag. Used by the snapshot decoder after populating
    /// all fields directly — avoids a throwaway round-trip through SeedFromSnapshot.
    /// </summary>
    internal void MarkSeeded() => IsSeeded = true;

    /// <summary>
    /// Install a baseline previously persisted in a snapshot. Copies the
    /// raw physical-depth rows as-is — no Geth shift conversion (the snapshot is
    /// already in unshifted form).
    /// </summary>
    public void SeedFromSnapshot(CumulativeDepthStats source)
    {
        Reset();
        for (int s = 0; s < SlotCount; s++)
            Array.Copy(source.ByDepth[s], ByDepth[s], DepthCount);
        TotalBranchNodes = source.TotalBranchNodes;
        TotalBranchChildren = source.TotalBranchChildren;
        IsSeeded = true;
    }

    /// <summary>Deep copy. Used for snapshot persistence.</summary>
    public CumulativeDepthStats Clone()
    {
        CumulativeDepthStats c = new();
        for (int s = 0; s < SlotCount; s++)
            Array.Copy(ByDepth[s], c.ByDepth[s], DepthCount);
        c.TotalBranchNodes = TotalBranchNodes;
        c.TotalBranchChildren = TotalBranchChildren;
        c.IsSeeded = IsSeeded;
        return c;
    }
}
