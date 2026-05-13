// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;

namespace Nethermind.StateComposition;

[ConfigCategory(Description = "State composition metrics")]
public interface IStateCompositionConfig : IConfig
{
    [ConfigItem(Description = "Enable state composition plugin", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Timeout in seconds to wait for a queued scan to acquire the lock. " +
                              "0 or negative means fail-fast (no wait).",
        DefaultValue = "5")]
    int ScanQueueTimeoutSeconds { get; set; }

    [ConfigItem(Description = "Max parallel threads for baseline trie scan. Clamped to [1, 16].",
        DefaultValue = "ProcessorCount/2")]
    int ScanParallelism { get; set; }

    [ConfigItem(Description = "Memory budget for baseline scan in raw bytes (no suffix parsing). " +
                              "Minimum useful value ~1 MiB (1048576). Default 1 GB.",
        DefaultValue = "1000000000")]
    long ScanMemoryBudgetBytes { get; set; }

    [ConfigItem(Description = "Number of top contracts to track per ranking category. Clamped to [1, 10000].",
        DefaultValue = "20")]
    int TopNContracts { get; set; }

    [ConfigItem(Description = "Skip storage trie traversal during scans",
        DefaultValue = "false")]
    bool ExcludeStorage { get; set; }

    [ConfigItem(Description = "Persist incremental stats snapshots to disk for warm restart",
        DefaultValue = "true")]
    bool PersistSnapshots { get; set; }

    [ConfigItem(Description = "Track per-depth trie distribution incrementally on every new head block",
        DefaultValue = "true")]
    bool TrackDepthIncrementally { get; set; }

    [ConfigItem(Description = "Timeout in seconds for a single statecomp_inspectContract RPC call. " +
                              "Long storage-trie walks that exceed this deadline are cancelled so " +
                              "the RPC worker is not pinned indefinitely. 0 or negative disables the timeout.",
        DefaultValue = "30")]
    int InspectContractTimeoutSeconds { get; set; }

    [ConfigItem(Description = "Persist a snapshot every N successful incremental diffs (overwrites). " +
                              "Bounds rescan loss to ≤ N blocks if the chain regresses past FlatDb's " +
                              "bundle window before the next graceful shutdown. Each persist writes " +
                              "all three contract-keyed tracker maps (~tens of GB at mainnet scale), " +
                              "so a tight interval generates heavy RocksDB write pressure: at 100, a " +
                              "long bloating run accumulated 1.6 TB of SSTs (4300+ files) over half " +
                              "a day because RocksDB compaction couldn't keep pace with " +
                              "delete-after-write churn. Graceful StopAsync flushes regardless of " +
                              "this counter, so on a clean shutdown there is no data loss; the " +
                              "trade-off applies only on hard kill.",
        DefaultValue = "10000")]
    int SnapshotIntervalDiffs { get; set; }
}

public class StateCompositionConfig : IStateCompositionConfig
{
    public bool Enabled { get; set; }
    public int ScanQueueTimeoutSeconds { get; set; } = 5;
    public int ScanParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 16);
    public long ScanMemoryBudgetBytes { get; set; } = 1_000_000_000;
    public int TopNContracts { get; set; } = 20;
    public bool ExcludeStorage { get; set; }
    public bool PersistSnapshots { get; set; } = true;
    public bool TrackDepthIncrementally { get; set; } = true;
    public int InspectContractTimeoutSeconds { get; set; } = 30;
    // 100× the prior default: each persist writes ~tens of GB; tighter intervals
    // overwhelm RocksDB compaction and accumulate orphaned SSTs that take hours
    // to clean up. Graceful shutdown flushes regardless of this counter.
    public int SnapshotIntervalDiffs { get; set; } = 10_000;
}
