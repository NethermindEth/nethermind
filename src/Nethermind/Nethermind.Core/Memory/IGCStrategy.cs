// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Core.Memory;

/// <summary>Controls no-GC-region entry and delayed post-payload collections.</summary>
public interface IGCStrategy
{
    /// <summary>Gets the payload interval for decommit: -1 disables it, 0 requests it every time, and positive values count eligible payload calls.</summary>
    /// <remarks>Calls disallowed by this strategy do not count; eligible calls with skipped entry or cancelled collections still count. Once due, decommit remains due until accepted,
    /// and runs only while <see cref="DecommitMemoryLoadPercent"/> is reached or once <see cref="CollectionsPerUnconditionalDecommit"/> payloads have been counted.</remarks>
    int CollectionsPerDecommit { get; }
    /// <summary>Gets the memory load, in percent of physical memory or of the container's memory limit, at which a due decommit runs; 0 or less runs it whenever due.</summary>
    /// <remarks>The load is the one the runtime recorded at its last collection (<see cref="System.GCMemoryInfo.MemoryLoadBytes"/>).
    /// Defaults to 0, so a strategy that does not implement it decommits whenever due.</remarks>
    int DecommitMemoryLoadPercent => 0;
    /// <summary>Gets the payload count after which a due decommit runs below <see cref="DecommitMemoryLoadPercent"/>: -1 never; a count below <see cref="CollectionsPerDecommit"/> runs it whenever due.</summary>
    /// <remarks>Defaults to -1; not read while <see cref="DecommitMemoryLoadPercent"/> is 0 or less.</remarks>
    int CollectionsPerUnconditionalDecommit => -1;
    /// <summary>Gets the delay in milliseconds before attempting a post-payload collection.</summary>
    int PostBlockDelayMs { get; }
    /// <summary>Gets whether the keeper may shorten <see cref="PostBlockDelayMs"/> to fit the idle gap payloads leave.</summary>
    /// <remarks>Defaults to <c>false</c>, so a strategy that does not implement it keeps a fixed delay.</remarks>
    bool AdaptivePostBlockDelay => false;
    /// <summary>Gets whether an eligible payload enters a runtime no-GC region; the keeper's collections run either way.</summary>
    /// <remarks>Defaults to <see cref="NoGcRegionMode.Always"/>.</remarks>
    NoGcRegionMode NoGcRegionOnNewPayload => NoGcRegionMode.Always;
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

public enum NoGcRegionMode
{
    [Description("Enters a no-GC region for every eligible `engine_newPayload` call.")]
    Always,
    [Description("Never enters a no-GC region; the call still holds off scheduled collections and is followed by the post-block collection.")]
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
