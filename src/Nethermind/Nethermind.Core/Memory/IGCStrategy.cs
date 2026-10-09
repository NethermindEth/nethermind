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
    /// <remarks>
    /// Also gates the post-payload collection and the decommit count: a payload disallowed here schedules neither,
    /// whatever <see cref="NoGCRegionMode"/> says.
    /// </remarks>
    bool CanStartNoGCRegion();
    /// <summary>Gets when a permitted payload enters a region of its own; one that does not still schedules the post-payload collection.</summary>
    NoGcRegionMode NoGCRegionMode { get; }
    /// <summary>
    /// Gets the most bytes that may be allocated after the region's budget was re-armed for a payload to skip its own
    /// entry with <see cref="NoGcRegionMode.Guard"/>, or 0 for the default (<see cref="GCKeeper.DefaultGuardSlack"/>).
    /// </summary>
    long NoGCRegionGuardBytes { get; }
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

public enum NoGcRegionMode
{
    [Description("Enters the no-GC region on every `engine_newPayload`.")]
    Always,
    [Description("Re-arms the no-GC region's budget right after the block on a quiet node and skips the entry while it is still armed.")]
    Guard,
    [Description("Never enters the no-GC region.")]
    Never
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
