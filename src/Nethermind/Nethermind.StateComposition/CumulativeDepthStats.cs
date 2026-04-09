// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.StateComposition;

/// <summary>
/// Mutable cumulative per-depth trie node counters.
/// One instance owned by <see cref="StateCompositionStateHolder"/>;
/// updated in-place under the holder's lock via <see cref="ApplyDelta"/>.
///
/// Physical-depth storage: arrays are indexed by physical depth [0..15].
/// The Geth +1 shift for ValueNodeCount is applied only at presentation time
/// in <see cref="Metrics.UpdateFromDepthStats"/> — never stored here.
///
/// AccountShortNodes[d] = extensions + leaves at physical depth d (matches Geth ShortNode convention).
/// AccountValueNodes[d] = leaves at physical depth d (unshifted).
/// </summary>
public sealed class CumulativeDepthStats
{
    // Per-depth account trie counters, indices [0..15]
    public long[] AccountFullNodes  { get; } = new long[16];
    public long[] AccountShortNodes { get; } = new long[16];
    public long[] AccountValueNodes { get; } = new long[16];
    public long[] AccountNodeBytes  { get; } = new long[16];

    // Per-depth storage trie counters, indices [0..15]
    public long[] StorageFullNodes  { get; } = new long[16];
    public long[] StorageShortNodes { get; } = new long[16];
    public long[] StorageValueNodes { get; } = new long[16];
    public long[] StorageNodeBytes  { get; } = new long[16];

    /// <summary>Branch occupancy histogram: index i = count of account-trie branches with (i+1) children.</summary>
    public long[] BranchOccupancy    { get; } = new long[16];
    public long TotalBranchNodes    { get; set; }
    public long TotalBranchChildren { get; set; }

    /// <summary>
    /// True once a baseline has been installed via <see cref="SeedFromScan"/> or
    /// <see cref="SeedFromSnapshot"/>. Until that happens, <see cref="ApplyDelta"/>
    /// is a no-op — applying deltas on top of an unseeded (all-zero) baseline would
    /// push gauges negative whenever a diff removes more nodes than it adds at a
    /// given depth. The gate guarantees gauges stay at 0 until a correct baseline is
    /// available, eliminating the "negative metrics" failure mode across restarts
    /// and cold starts.
    /// </summary>
    public bool IsSeeded { get; private set; }

    /// <summary>Apply a diff delta in-place. Allocation-free on the hot path.</summary>
    /// <remarks>No-op until <see cref="IsSeeded"/> is true.</remarks>
    public void ApplyDelta(DepthDelta d)
    {
        if (!IsSeeded) return;
        for (int i = 0; i < 16; i++)
        {
            AccountFullNodes[i]  += d.AccountFullNodes[i];
            AccountShortNodes[i] += d.AccountShortNodes[i];
            AccountValueNodes[i] += d.AccountValueNodes[i];
            AccountNodeBytes[i]  += d.AccountNodeBytes[i];
            StorageFullNodes[i]  += d.StorageFullNodes[i];
            StorageShortNodes[i] += d.StorageShortNodes[i];
            StorageValueNodes[i] += d.StorageValueNodes[i];
            StorageNodeBytes[i]  += d.StorageNodeBytes[i];
            BranchOccupancy[i]   += d.BranchOccupancy[i];
        }
        TotalBranchNodes    += d.TotalBranchNodesDelta;
        TotalBranchChildren += d.TotalBranchChildrenDelta;
    }

    /// <summary>Zero all arrays and scalars. Used when a fresh scan re-baselines.</summary>
    public void Reset()
    {
        Array.Clear(AccountFullNodes);
        Array.Clear(AccountShortNodes);
        Array.Clear(AccountValueNodes);
        Array.Clear(AccountNodeBytes);
        Array.Clear(StorageFullNodes);
        Array.Clear(StorageShortNodes);
        Array.Clear(StorageValueNodes);
        Array.Clear(StorageNodeBytes);
        Array.Clear(BranchOccupancy);
        TotalBranchNodes    = 0;
        TotalBranchChildren = 0;
        IsSeeded            = false;
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
            int d = stat.Depth < 16 ? stat.Depth : 15;
            AccountFullNodes[d]  = stat.FullNodeCount;
            AccountShortNodes[d] = stat.ShortNodeCount;
            AccountNodeBytes[d]  = stat.TotalSize;
            // ValueNodeCount in stat is shifted: stat.ValueNodeCount = physical leaves at depth d-1
            // Reverse the shift: store physical leaves at depth d-1
            if (d > 0)
                AccountValueNodes[d - 1] = stat.ValueNodeCount;
        }

        foreach (TrieLevelStat stat in dist.StorageTrieLevels)
        {
            int d = stat.Depth < 16 ? stat.Depth : 15;
            StorageFullNodes[d]  = stat.FullNodeCount;
            StorageShortNodes[d] = stat.ShortNodeCount;
            StorageNodeBytes[d]  = stat.TotalSize;
            if (d > 0)
                StorageValueNodes[d - 1] = stat.ValueNodeCount;
        }

        for (int i = 0; i < dist.BranchOccupancyDistribution.Length && i < 16; i++)
            BranchOccupancy[i] = dist.BranchOccupancyDistribution[i];

        // Derive TotalBranchNodes and TotalBranchChildren from the occupancy histogram
        // (index i = branches with (i+1) children)
        long nodes = 0, children = 0;
        for (int i = 0; i < 16; i++)
        {
            nodes    += BranchOccupancy[i];
            children += BranchOccupancy[i] * (i + 1);
        }
        TotalBranchNodes    = nodes;
        TotalBranchChildren = children;
        IsSeeded            = true;
    }

    /// <summary>
    /// Install a baseline previously persisted in a snapshot (Phase C). Copies the
    /// raw physical-depth arrays as-is — no Geth shift conversion (the snapshot is
    /// already in unshifted form).
    /// </summary>
    public void SeedFromSnapshot(CumulativeDepthStats source)
    {
        Reset();
        Array.Copy(source.AccountFullNodes,  AccountFullNodes,  16);
        Array.Copy(source.AccountShortNodes, AccountShortNodes, 16);
        Array.Copy(source.AccountValueNodes, AccountValueNodes, 16);
        Array.Copy(source.AccountNodeBytes,  AccountNodeBytes,  16);
        Array.Copy(source.StorageFullNodes,  StorageFullNodes,  16);
        Array.Copy(source.StorageShortNodes, StorageShortNodes, 16);
        Array.Copy(source.StorageValueNodes, StorageValueNodes, 16);
        Array.Copy(source.StorageNodeBytes,  StorageNodeBytes,  16);
        Array.Copy(source.BranchOccupancy,   BranchOccupancy,   16);
        TotalBranchNodes    = source.TotalBranchNodes;
        TotalBranchChildren = source.TotalBranchChildren;
        IsSeeded            = true;
    }

    /// <summary>Deep copy. Used for snapshot persistence.</summary>
    public CumulativeDepthStats Clone()
    {
        CumulativeDepthStats c = new();
        Array.Copy(AccountFullNodes,  c.AccountFullNodes,  16);
        Array.Copy(AccountShortNodes, c.AccountShortNodes, 16);
        Array.Copy(AccountValueNodes, c.AccountValueNodes, 16);
        Array.Copy(AccountNodeBytes,  c.AccountNodeBytes,  16);
        Array.Copy(StorageFullNodes,  c.StorageFullNodes,  16);
        Array.Copy(StorageShortNodes, c.StorageShortNodes, 16);
        Array.Copy(StorageValueNodes, c.StorageValueNodes, 16);
        Array.Copy(StorageNodeBytes,  c.StorageNodeBytes,  16);
        Array.Copy(BranchOccupancy,   c.BranchOccupancy,   16);
        c.TotalBranchNodes    = TotalBranchNodes;
        c.TotalBranchChildren = TotalBranchChildren;
        c.IsSeeded            = IsSeeded;
        return c;
    }
}
