// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Merge.Plugin.GC;

public interface IGCStrategy
{
    int CollectionsPerDecommit { get; }
    bool CanStartNoGCRegion();
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
