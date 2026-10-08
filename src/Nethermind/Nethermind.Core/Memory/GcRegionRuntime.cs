// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Runtime;

namespace Nethermind.Core.Memory;

internal interface IGcRegionRuntime
{
    bool IsActive { get; }
    bool TryStart(long totalSize, long lohSize);
    void End();
    /// <summary>Bytes allocated by the process so far, as cheaply as the runtime reports it (not precise).</summary>
    long AllocatedBytes { get; }
    bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting);
    /// <summary>BENCH: the gen0 allocation budget summed over the heaps, or -1 when the runtime does not tell.</summary>
    long Gen0Budget { get; }
    /// <summary>BENCH: the index of the last collection; a region entry collects nothing and leaves it.</summary>
    long LastGcIndex { get; }
    /// <summary>BENCH: <see cref="System.GC.CollectionCount"/>, which a region entry moves by one per generation.</summary>
    int CollectionCount(int generation);
}

internal sealed class GcRegionRuntime : IGcRegionRuntime
{
    internal static readonly GcRegionRuntime Instance = new();

    public bool IsActive => GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
    public bool TryStart(long totalSize, long lohSize) =>
        System.GC.TryStartNoGCRegion(totalSize, lohSize, disallowFullBlockingGC: true);
    public void End() => System.GC.EndNoGCRegion();
    public long AllocatedBytes => System.GC.GetTotalAllocatedBytes(precise: false);
    /// <summary>Requests a collection through the scheduler, applying the requested compaction policy.</summary>
    /// <remarks>Aggressive GC enables LOH compaction itself (dotnet/runtime v10.0.0, gc.cpp: reason_induced_aggressive).</remarks>
    public bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting) =>
        GCScheduler.Instance.GCCollect((int)generation, mode, blocking: compacting > GcCompaction.No,
            compacting: compacting > GcCompaction.No, trimNativeMemory: true,
            compactLoh: mode != GCCollectionMode.Aggressive && generation == GcLevel.Gen2 && compacting == GcCompaction.Full);

    // System.GC.GetGenerationBudget is internal (dotnet/runtime v10.0.0 GC.CoreCLR.cs); it sums dd_desired_allocation
    // over the heaps (gcee.cpp GCHeap::GetGenerationBudget). GCMemoryInfo exposes no budget.
    private static readonly Func<int, long>? _generationBudget = typeof(System.GC)
        .GetMethod("GetGenerationBudget", BindingFlags.Static | BindingFlags.NonPublic, [typeof(int)])
        ?.CreateDelegate<Func<int, long>>();

    public long Gen0Budget
    {
        get
        {
            try
            {
                return _generationBudget?.Invoke(0) ?? -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }

    // Recorded only when a collection runs (gc.cpp do_post_gc); a region entry that collects nothing skips it.
    public long LastGcIndex => System.GC.GetGCMemoryInfo(GCKind.Any).Index;
    public int CollectionCount(int generation) => System.GC.CollectionCount(generation);
}
