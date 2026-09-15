// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;

namespace Nethermind.Core.Memory;

internal interface IGcRegionRuntime
{
    bool IsActive { get; }
    bool TryStart(long totalSize, long lohSize);
    void End();
    bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting);
}

internal sealed class GcRegionRuntime : IGcRegionRuntime
{
    internal static readonly GcRegionRuntime Instance = new();

    public bool IsActive => GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
    public bool TryStart(long totalSize, long lohSize) =>
        System.GC.TryStartNoGCRegion(totalSize, lohSize, disallowFullBlockingGC: true);
    public void End() => System.GC.EndNoGCRegion();
    /// <remarks>Aggressive GC enables LOH compaction itself (dotnet/runtime v10.0.0, gc.cpp: reason_induced_aggressive).</remarks>
    public bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting) =>
        GCScheduler.Instance.GCCollect((int)generation, mode, blocking: compacting > GcCompaction.No,
            compacting: compacting > GcCompaction.No, trimNativeMemory: true,
            compactLoh: mode != GCCollectionMode.Aggressive && generation == GcLevel.Gen2 && compacting == GcCompaction.Full);
}
