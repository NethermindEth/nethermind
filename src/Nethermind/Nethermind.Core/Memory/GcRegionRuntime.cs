// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime;

namespace Nethermind.Core.Memory;

internal interface IGcRegionRuntime
{
    bool IsActive { get; }
    bool TryStart(long totalSize, long lohSize);
    void End();
    bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting);
    /// <summary>The gen2 collections the runtime has started, background ones included; a no-GC region entry counts as one.</summary>
    int Gen2CollectionCount { get; }
    /// <summary>Returns the memory load the runtime recorded at its last collection.</summary>
    GcMemoryLoad GetMemoryLoad();
}

/// <param name="MemoryLoadBytes">The memory in use, machine-wide or within the container's memory limit.</param>
/// <param name="TotalMemoryBytes">The memory that load is a share of: physical memory or the container's memory limit; 0 when unknown.</param>
internal readonly record struct GcMemoryLoad(long MemoryLoadBytes, long TotalMemoryBytes)
{
    /// <summary>Pairs the load the runtime recorded at its last collection with the memory it is a share of.</summary>
    /// <param name="memoryLoadBytes"><see cref="GCMemoryInfo.MemoryLoadBytes"/>.</param>
    /// <param name="highMemoryLoadThresholdBytes"><see cref="GCMemoryInfo.HighMemoryLoadThresholdBytes"/>.</param>
    /// <param name="gcConfiguration"><see cref="System.GC.GetConfigurationVariables"/>.</param>
    /// <remarks>
    /// Not <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/>: that is the GC heap hard limit once one is set, and in a container
    /// with a memory limit the runtime sets one by default at 75% of that limit. The memory load and the high memory load threshold
    /// are both percentages of physical memory or the container limit, the threshold of the <c>GCHighMemPercent</c> the runtime applies
    /// (dotnet/runtime v10.0.13, gc.cpp: GCHeap::GetMemoryInfo and gc_heap::compute_memory_settings).
    /// </remarks>
    internal static GcMemoryLoad FromRecorded(long memoryLoadBytes, long highMemoryLoadThresholdBytes, IReadOnlyDictionary<string, object> gcConfiguration)
    {
        long highMemoryPercent = gcConfiguration.TryGetValue("GCHighMemPercent", out object? value) && value is long percent ? percent : 0;
        return new(memoryLoadBytes, highMemoryPercent > 0 && highMemoryLoadThresholdBytes > 0 ? highMemoryLoadThresholdBytes * 100 / highMemoryPercent : 0);
    }
}

internal sealed class GcRegionRuntime : IGcRegionRuntime
{
    internal static readonly GcRegionRuntime Instance = new();
    // Fixed once the runtime has started (gc.cpp compute_memory_settings), so read once rather than per check.
    private static readonly IReadOnlyDictionary<string, object> GcConfiguration = System.GC.GetConfigurationVariables();

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
    /// <remarks>Counted when a collection starts, so a background gen2 is counted before the call that started it returns
    /// (dotnet/runtime v10.0.0, gc.cpp: gc1 updates the counts before background_mark_phase lets the triggering thread go).</remarks>
    public int Gen2CollectionCount => System.GC.CollectionCount(2);
    public GcMemoryLoad GetMemoryLoad()
    {
        GCMemoryInfo info = System.GC.GetGCMemoryInfo();
        return GcMemoryLoad.FromRecorded(info.MemoryLoadBytes, info.HighMemoryLoadThresholdBytes, GcConfiguration);
    }
}
