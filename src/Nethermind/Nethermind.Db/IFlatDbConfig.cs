// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

public interface IFlatDbConfig : IConfig
{
    [ConfigItem(Description = "Block cache size budget", DefaultValue = "1073741824")]
    ulong BlockCacheSizeBudget { get; set; }

    [ConfigItem(Description = "Fixed compaction schedule offset in blocks. When 0 or greater, overrides the per-instance offset in the metadata DB, which is neither read nor updated. Only the value modulo CompactSize matters. -1 to use the stored offset, generating a random one when absent.", DefaultValue = "-1")]
    long CompactionOffset { get; set; }

    [ConfigItem(Description = "Compact size", DefaultValue = "32")]
    ulong CompactSize { get; set; }

    [ConfigItem(Description = "Enabled", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Enable recording of preimages (address/slot hash to original bytes)", DefaultValue = "false")]
    bool EnablePreimageRecording { get; set; }

    [ConfigItem(Description = "Import from pruning trie state db", DefaultValue = "false")]
    bool ImportFromPruningTrieState { get; set; }

    [ConfigItem(Description = "Inline compaction", DefaultValue = "false")]
    bool InlineCompaction { get; set; }

    [ConfigItem(Description = "Flat db layout", DefaultValue = "Flat")]
    FlatLayout Layout { get; set; }

    [ConfigItem(Description = "Max in flight compact job", DefaultValue = "32")]
    int MaxInFlightCompactJob { get; set; }

    [ConfigItem(Description = "Max reorg depth", DefaultValue = "256")]
    ulong MaxReorgDepth { get; set; }

    [ConfigItem(Description = "Minimum reorg depth", DefaultValue = "128")]
    ulong MinReorgDepth { get; set; }

    [ConfigItem(Description = "Lower bound, in bytes, for the RocksDB write buffer (memtable) size of the flat-state columns. The per-batch adjuster never shrinks a column's memtable below this value. Raising it lets frequent small persistence batches (small CompactSize) coalesce and deduplicate in the memtable instead of churning L0, decoupling write amplification from CompactSize.", DefaultValue = "16777216")]
    long PersistenceWriteBufferFloor { get; set; }

    [ConfigItem(Description = "Regenerate the per-instance compaction offset on startup instead of loading from metadata DB. Use when restoring one backup to multiple instances. Flag is sticky across restarts — toggle off after first restart.", DefaultValue = "false")]
    bool RegenerateCompactionOffset { get; set; }

    [ConfigItem(Description = "Trie cache memory target", DefaultValue = "536870912")]
    ulong TrieCacheMemoryBudget { get; set; }

    [ConfigItem(Description = "Trie warmer worker count (-1 for processor count - 1, 0 to disable)", DefaultValue = "-1")]
    int TrieWarmerWorkerCount { get; set; }

    [ConfigItem(Description = "Maximum number of already-queued same-contract slot warmer jobs to coalesce into one batched flat-storage read. 1 disables batching.", DefaultValue = "1")]
    int TrieWarmerBatchSize { get; set; }

    [ConfigItem(Description = "Verify with trie", DefaultValue = "false")]
    bool VerifyWithTrie { get; set; }

    [ConfigItem(Description = "Persistent dedicated reader threads used to resolve hinted BAL read sets into the pre-block cache. -1 for 4x logical processor count capped at 64. Values below 1 are clamped to 1. Use --Blocks.ParallelExecutionBatchRead=false to disable BAL warming entirely.", DefaultValue = "-1")]
    int WarmReadConcurrency { get; set; }
}
