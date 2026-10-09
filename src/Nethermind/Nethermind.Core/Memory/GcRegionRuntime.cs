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
    bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting);
    /// <summary>Collections the runtime has run so far, of any generation; a region entry also counts as one.</summary>
    int CollectionCount { get; }
    /// <summary>Bytes allocated by the process so far, as cheaply as the runtime reports it (not precise).</summary>
    long AllocatedBytes { get; }
    /// <summary>Gen0's allocation budget summed over the heaps, or -1 when the runtime does not tell.</summary>
    long Gen0Budget { get; }
    /// <summary>Whether the runtime exposes <see cref="Gen0Budget"/> at all; a budget it exposes can still read 0 or less.</summary>
    bool CanReadGen0Budget { get; }
    /// <summary>The index of the last collection; a region entry collects nothing and leaves it.</summary>
    long LastGcIndex { get; }
}

internal sealed class GcRegionRuntime : IGcRegionRuntime
{
    internal static readonly GcRegionRuntime Instance = new();

    public bool IsActive => GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
    public bool TryStart(long totalSize, long lohSize) =>
        System.GC.TryStartNoGCRegion(totalSize, lohSize, disallowFullBlockingGC: true);
    public void End() => System.GC.EndNoGCRegion();
    /// <summary>Requests a collection through the scheduler, applying the requested compaction policy.</summary>
    /// <remarks>Aggressive GC enables LOH compaction itself (dotnet/runtime v10.0.0, gc.cpp: reason_induced_aggressive).</remarks>
    public bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting) =>
        GCScheduler.Instance.GCCollect((int)generation, mode, blocking: compacting > GcCompaction.No,
            compacting: compacting > GcCompaction.No, trimNativeMemory: true,
            compactLoh: mode != GCCollectionMode.Aggressive && generation == GcLevel.Gen2 && compacting == GcCompaction.Full);
    public int CollectionCount => System.GC.CollectionCount(0);
    public long AllocatedBytes => System.GC.GetTotalAllocatedBytes(precise: false);

    // System.GC.GetGenerationBudget is internal (dotnet/runtime v10.0.0, GC.CoreCLR.cs); it sums dd_desired_allocation
    // over the heaps (gcee.cpp, GCHeap::GetGenerationBudget). GCMemoryInfo exposes no budget.
    private static readonly Func<int, long>? _generationBudget = CreateGenerationBudget();

    public bool CanReadGen0Budget => _generationBudget is not null;

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

    private static Func<int, long>? CreateGenerationBudget()
    {
        try
        {
            Func<int, long>? budget = typeof(System.GC)
                .GetMethod("GetGenerationBudget", BindingFlags.Static | BindingFlags.NonPublic, [typeof(int)])
                ?.CreateDelegate<Func<int, long>>();
            // Probed once: a changed signature or an internal call the runtime refuses then reads as unavailable,
            // instead of failing the type initializer or every payload.
            budget?.Invoke(0);
            return budget;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Recorded only when a collection runs (gc.cpp, do_post_gc); a region entry, which collects nothing, leaves it.
    public long LastGcIndex => System.GC.GetGCMemoryInfo(GCKind.Any).Index;
}
