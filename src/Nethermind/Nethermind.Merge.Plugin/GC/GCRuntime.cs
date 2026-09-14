// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.GC;

/// <summary>The runtime operations the keeper drives, so its scheduling can be tested without a real no-GC region.</summary>
internal interface IGCRuntime
{
    bool InNoGCRegion { get; }
    bool TryStartNoGCRegion(long totalSize, long lohSize);
    void EndNoGCRegion();
    void CompactLargeObjectHeapOnce();
    bool Collect(int generation, GCCollectionMode mode, bool compacting, bool trimNativeMemory);
}

internal sealed class GCRuntime : IGCRuntime
{
    public static readonly GCRuntime Instance = new();

    private GCRuntime() { }

    public bool InNoGCRegion => GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;

    // A full blocking GC to make room would defeat the purpose; the runtime then reports failure instead.
    public bool TryStartNoGCRegion(long totalSize, long lohSize) => System.GC.TryStartNoGCRegion(totalSize, lohSize, disallowFullBlockingGC: true);

    public void EndNoGCRegion() => System.GC.EndNoGCRegion();

    public void CompactLargeObjectHeapOnce() => GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

    public bool Collect(int generation, GCCollectionMode mode, bool compacting, bool trimNativeMemory) =>
        GCScheduler.Instance.GCCollect(generation, mode, blocking: true, compacting, trimNativeMemory);
}
