// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Visitors;

namespace Nethermind.StateComposition.Diff;

/// <summary>
/// Fixed slot index for the nine per-depth counter rows in
/// <see cref="CumulativeDepthStats"/>. The numeric order is load-bearing:
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
/// Storage layout: a single <c>9 × 16</c> inline buffer (9 × <see cref="Long16"/> = 144
/// contiguous longs), indexed by <see cref="DepthSlot"/>. Cumulative ops (Reset, Clone,
/// AddInPlace, IsEmpty, SeedFromSnapshot) walk the flat span in one loop.
///
/// Physical-depth storage: rows are indexed by physical depth [0..15].
/// The Geth +1 depth shift for ValueNodeCount is applied only at presentation
/// time in <see cref="Metrics.UpdateDepthDistribution"/> — never stored here.
///
/// Uses Geth vocabulary (see <see cref="TrieLevelStat"/> for full mapping):
/// AccountShortNodes[d] = extensions + leaves at physical depth d.
/// AccountValueNodes[d] = leaves at physical depth d (unshifted).
/// </summary>
public sealed class CumulativeDepthStats
{
    public const int CategoryCount = 9;
    public const int DepthCount = Long16.Length;
    private const int TotalLongs = CategoryCount * DepthCount;

    [InlineArray(CategoryCount)]
    private struct DepthRows9
    {
        private Long16 _row;
    }

    private DepthRows9 _rows;

    public Span<long> GetRow(int slotIndex) => MemoryMarshal.CreateSpan(
        ref Unsafe.Add(ref Unsafe.As<DepthRows9, long>(ref _rows), slotIndex * DepthCount),
        DepthCount);

    public Span<long> AccountFullNodes => GetRow((int)DepthSlot.AccountFull);
    public Span<long> AccountShortNodes => GetRow((int)DepthSlot.AccountShort);
    public Span<long> AccountValueNodes => GetRow((int)DepthSlot.AccountValue);
    public Span<long> AccountNodeBytes => GetRow((int)DepthSlot.AccountBytes);
    public Span<long> StorageFullNodes => GetRow((int)DepthSlot.StorageFull);
    public Span<long> StorageShortNodes => GetRow((int)DepthSlot.StorageShort);
    public Span<long> StorageValueNodes => GetRow((int)DepthSlot.StorageValue);
    public Span<long> StorageNodeBytes => GetRow((int)DepthSlot.StorageBytes);
    /// <summary>Branch occupancy histogram: index i = count of account-trie branches with (i+1) children.</summary>
    public Span<long> BranchOccupancy => GetRow((int)DepthSlot.BranchOccupancy);

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

    private Span<long> FlatRows => MemoryMarshal.CreateSpan(
        ref Unsafe.As<DepthRows9, long>(ref _rows), TotalLongs);

    /// <remarks>No-op until this instance's <see cref="IsSeeded"/> is true.</remarks>
    public void AddInPlace(CumulativeDepthStats other)
    {
        if (!IsSeeded) return;
        Span<long> dst = FlatRows;
        ReadOnlySpan<long> src = other.FlatRows;
        for (int i = 0; i < TotalLongs; i++) dst[i] += src[i];
        TotalBranchNodes += other.TotalBranchNodes;
        TotalBranchChildren += other.TotalBranchChildren;
    }

    public bool IsEmpty()
    {
        if (TotalBranchNodes != 0 || TotalBranchChildren != 0) return false;
        foreach (long v in FlatRows) if (v != 0) return false;
        return true;
    }

    public void Reset()
    {
        _rows = default;
        TotalBranchNodes = 0;
        TotalBranchChildren = 0;
        IsSeeded = false;
    }

    /// <summary>
    /// Conversion rules (reversing BuildLevelStats in StateCompositionVisitor):
    ///   stat.FullNodeCount  at depth d → AccountFullNodes[d]
    ///   stat.ShortNodeCount at depth d → AccountShortNodes[d]  (extensions+leaves at depth d)
    ///   stat.ValueNodeCount at depth d → AccountValueNodes[d-1] (shift reversed: physical depth = d-1)
    ///   stat.TotalSize      at depth d → AccountNodeBytes[d]
    /// </summary>
    public void SeedFromScan(TrieDepthDistribution dist)
    {
        Reset();

        Span<long> accFull = AccountFullNodes;
        Span<long> accShort = AccountShortNodes;
        Span<long> accValue = AccountValueNodes;
        Span<long> accBytes = AccountNodeBytes;
        foreach (TrieLevelStat stat in dist.AccountTrieLevels)
        {
            int d = stat.Depth < DepthCount ? stat.Depth : DepthCount - 1;
            accFull[d] = stat.FullNodeCount;
            accShort[d] = stat.ShortNodeCount;
            accBytes[d] = stat.TotalSize;
            if (d > 0) accValue[d - 1] = stat.ValueNodeCount;
        }

        Span<long> stoFull = StorageFullNodes;
        Span<long> stoShort = StorageShortNodes;
        Span<long> stoValue = StorageValueNodes;
        Span<long> stoBytes = StorageNodeBytes;
        foreach (TrieLevelStat stat in dist.StorageTrieLevels)
        {
            int d = stat.Depth < DepthCount ? stat.Depth : DepthCount - 1;
            stoFull[d] = stat.FullNodeCount;
            stoShort[d] = stat.ShortNodeCount;
            stoBytes[d] = stat.TotalSize;
            if (d > 0) stoValue[d - 1] = stat.ValueNodeCount;
        }

        Span<long> branch = BranchOccupancy;
        for (int i = 0; i < dist.BranchOccupancyDistribution.Length && i < DepthCount; i++)
            branch[i] = dist.BranchOccupancyDistribution[i];

        long nodes = 0, children = 0;
        for (int i = 0; i < DepthCount; i++)
        {
            nodes += branch[i];
            children += branch[i] * (i + 1);
        }
        TotalBranchNodes = nodes;
        TotalBranchChildren = children;
        IsSeeded = true;
    }

    internal void MarkSeeded() => IsSeeded = true;

    public void SeedFromSnapshot(CumulativeDepthStats source)
    {
        _rows = source._rows;
        TotalBranchNodes = source.TotalBranchNodes;
        TotalBranchChildren = source.TotalBranchChildren;
        IsSeeded = true;
    }

    /// <summary>
    /// Deep copy intended for handing a diff's DepthDelta to a consumer without
    /// sharing mutable state with the producer. Leaves <see cref="IsSeeded"/> at
    /// its default (false) — a delta is always accumulated into a seeded target,
    /// so the seeded flag is immaterial for consumers.
    /// </summary>
    public CumulativeDepthStats CloneAsDelta()
    {
        CumulativeDepthStats copy = new()
        {
            _rows = _rows,
            TotalBranchNodes = TotalBranchNodes,
            TotalBranchChildren = TotalBranchChildren,
        };
        return copy;
    }
}
