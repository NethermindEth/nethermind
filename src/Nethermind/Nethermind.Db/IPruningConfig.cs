// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;

namespace Nethermind.Db;

[ConfigCategory(Description = "Configuration of the pruning parameters (pruning is the process of removing some of the intermediary state nodes - it saves some disk space but makes most of the historical state queries fail).")]
public interface IPruningConfig : IConfig
{
    [ConfigItem(Description = "Enables in-memory pruning. Obsolete, use Mode instead.", DefaultValue = "true", HiddenFromDocs = true)]
    [Obsolete]
    public bool Enabled { get; set; }

    [ConfigItem(Description = "The pruning mode.", DefaultValue = "Hybrid")]
    PruningMode Mode { get; set; }

    [ConfigItem(Description = "The in-memory cache size, in MB. Bigger size tend to improve performance.", DefaultValue = "1280")]
    long CacheMb { get; set; }

    [ConfigItem(Description = "The in-memory cache size for dirty nodes, in MB. Increasing this reduces pruning interval but cause increased pruning time.", DefaultValue = "1024")]
    long DirtyCacheMb { get; set; }

    [ConfigItem(
        Description = "The block persistence frequency. If set to `N`, it caches after each `Nth` block even if not required by cache memory usage.",
        DefaultValue = "8192")]
    long PersistenceInterval { get; set; }

    [ConfigItem(
        Description = $"The threshold, in MB, to trigger full pruning. Depends on `{nameof(Mode)}` and `{nameof(FullPruningTrigger)}`.",
        DefaultValue = "256000")]
    long FullPruningThresholdMb { get; set; }

    [ConfigItem(
        Description = "The full pruning trigger.", DefaultValue = "Manual")]
    FullPruningTrigger FullPruningTrigger { get; set; }

    [ConfigItem(
        Description = """
            The max number of parallel tasks that can be used by full pruning.

            Allowed values:

            - `-1`: Uses the number of logical processors.
            - `0`: Uses 25% of logical processors.
            - `1`: Runs on a single thread.

            The recommended value depends on the type of the node:

            - If the node needs to be responsive (serves for RPC or validator), then the recommended value is `0` or `-1`.
            - If the node doesn't have many other responsibilities but needs to be able to follow the chain reliably without any delays and produce live logs, the `0` or `1` is recommended.
            - If the node doesn't have to be responsive, has very fast I/O (like NVMe) and the shortest pruning time is to be achieved, then `-1` is recommended.
            """,
        DefaultValue = "0")]
    int FullPruningMaxDegreeOfParallelism { get; set; }

    [ConfigItem(
        Description = "The memory budget, in MB, used for the trie visit. Increasing this value significantly reduces the IOPS requirement at the expense of memory usage. `0` to disable.",
        DefaultValue = "4000")]
    int FullPruningMemoryBudgetMb { get; set; }

    [ConfigItem(
        Description = "Whether to disable low-priority for pruning writes. Full pruning uses low-priority write operations to prevent blocking block processing. If block processing is not high-priority, set this option to `true` for faster pruning.",
        DefaultValue = "false")]
    bool FullPruningDisableLowPriorityWrites { get; set; }

    [ConfigItem(Description = "The minimum delay, in hours, between full pruning operations not to exhaust disk writes.", DefaultValue = "240")]
    int FullPruningMinimumDelayHours { get; set; }

    [ConfigItem(Description = "The action to take on pruning completion.", DefaultValue = "None")]
    FullPruningCompletionBehavior FullPruningCompletionBehavior { get; set; }

    [ConfigItem(Description = "Whether to enables available disk space check.", DefaultValue = "true")]
    bool AvailableSpaceCheckEnabled { get; set; }

    [ConfigItem(Description = "_DEPRECATED_ Pruning trie store uses pruning cache as past keys.", DefaultValue = "0.1", HiddenFromDocs = true)]
    double TrackedPastKeyCountMemoryRatio { get; set; }

    [ConfigItem(Description = "Enable tracking of past key to reduce database and pruning cache growth", DefaultValue = "true")]
    bool TrackPastKeys { get; set; }

    [ConfigItem(Description = "The number of past states before the state gets pruned. Used to determine how old of a state to keep from the head.", DefaultValue = "64")]
    int PruningBoundary { get; set; }

    [ConfigItem(Description = "Dirty node shard count", DefaultValue = "8")]
    int DirtyNodeShardBit { get; set; }

    [ConfigItem(Description = "Portion of persisted node to be prune at a time", DefaultValue = "0.05")]
    double PrunePersistedNodePortion { get; set; }

    [ConfigItem(Description = "Minimum persisted cache prune target", DefaultValue = "50000000")]
    long PrunePersistedNodeMinimumTarget { get; set; }
}
