// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Core.Memory;

/// <summary>Controls no-GC-region entry and delayed post-payload collections.</summary>
public interface IGCStrategy
{
    /// <summary>Gets the payload interval for decommit: -1 disables it, 0 requests it every time, and positive values count eligible payload calls.</summary>
    /// <remarks>Calls disallowed by this strategy do not count; eligible calls with skipped entry or cancelled collections still count. Once due, decommit remains due until accepted.</remarks>
    int CollectionsPerDecommit { get; }
    /// <summary>Gets the delay in milliseconds before attempting a post-payload collection.</summary>
    int PostBlockDelayMs { get; }
    /// <summary>Returns whether no-GC-region entry is currently permitted.</summary>
    bool CanStartNoGCRegion();
    /// <summary>Returns ordinary collection settings; NoGC disables scheduling and a due decommit overrides these settings.</summary>
    (GcLevel Generation, GcCompaction Compacting) GetForcedGCParams();
}

public enum GcLevel
{
    [Description("Disables garbage collection.")]
    NoGC = -1,
    [Description("Enables garbage collection of generation 0.")]
    Gen0 = 0,
    [Description("Enables garbage collection of generation 1.")]
    Gen1 = 1,
    [Description("Enables garbage collection of generation 2.")]
    Gen2 = 2
}

public enum GcCompaction
{
    [Description("Disables memory compaction.")]
    No,
    [Description("Enables memory compaction.")]
    Yes,
    [Description($"Enables memory compaction with the large object heap (LOH) if `SweepMemory` is set to `{nameof(GcLevel.Gen2)}`.")]
    Full
}
