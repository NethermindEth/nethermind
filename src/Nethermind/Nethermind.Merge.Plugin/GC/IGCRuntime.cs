// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime;

namespace Nethermind.Merge.Plugin.GC;

/// <summary>The runtime's no-GC-region calls, seamed so <see cref="GCKeeper"/> can be tested without a live collector.</summary>
internal interface IGCRuntime
{
    bool TryStartNoGCRegion(long totalSize, long lohSize);
    bool IsInNoGCRegion { get; }
    void EndNoGCRegion();
}

internal sealed class GCRuntime : IGCRuntime
{
    public static readonly GCRuntime Instance = new();

    public bool TryStartNoGCRegion(long totalSize, long lohSize) =>
        System.GC.TryStartNoGCRegion(totalSize, lohSize, disallowFullBlockingGC: true);

    public bool IsInNoGCRegion => GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;

    public void EndNoGCRegion() => System.GC.EndNoGCRegion();
}
