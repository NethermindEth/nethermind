// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

public interface IFlatDbConfig : IConfig
{
    [ConfigItem(Description = "Block cache size budget", DefaultValue = "1073741824")]
    long BlockCacheSizeBudget { get; set; }

    [ConfigItem(Description = "Fixed compaction schedule offset in blocks. When 0 or greater, overrides the per-instance offset in the metadata DB, which is neither read nor updated. Only the value modulo CompactSize matters. -1 to use the stored offset, generating a random one when absent.", DefaultValue = "-1")]
    long CompactionOffset { get; set; }

    [ConfigItem(Description = "Compact size", DefaultValue = "32")]
    int CompactSize { get; set; }

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

    [ConfigItem(Description = "Max reorg depth — the force-persist backstop used when EnableLongFinality is off: once the in-memory depth exceeds it while finality is stalled, persistence is forced to bound memory.", DefaultValue = "256")]
    int MaxReorgDepth { get; set; }

    [ConfigItem(Description = "Minimum reorg depth", DefaultValue = "128")]
    int MinReorgDepth { get; set; }

    [ConfigItem(Description = "Lower bound, in bytes, for the RocksDB write buffer (memtable) size of the flat-state columns. The per-batch adjuster never shrinks a column's memtable below this value. Raising it lets frequent small persistence batches (small CompactSize) coalesce and deduplicate in the memtable instead of churning L0, decoupling write amplification from CompactSize.", DefaultValue = "16777216")]
    long PersistenceWriteBufferFloor { get; set; }

    [ConfigItem(Description = "Regenerate the per-instance compaction offset on startup instead of loading from metadata DB. Use when restoring one backup to multiple instances. Flag is sticky across restarts — toggle off after first restart.", DefaultValue = "false")]
    bool RegenerateCompactionOffset { get; set; }

    [ConfigItem(Description = "Trie cache memory target", DefaultValue = "536870912")]
    long TrieCacheMemoryBudget { get; set; }

    [ConfigItem(Description = "Trie warmer worker count (-1 for processor count - 1, 0 to disable)", DefaultValue = "-1")]
    int TrieWarmerWorkerCount { get; set; }

    [ConfigItem(Description = "Verify with trie", DefaultValue = "false")]
    bool VerifyWithTrie { get; set; }

    [ConfigItem(Description = "Enable long finality support with persisted snapshots", DefaultValue = "false")]
    bool EnableLongFinality { get; set; }

    [ConfigItem(Description = "Force-persist backstop used when EnableLongFinality is on, in place of MaxReorgDepth. The persisted-snapshot tier serves deep reorgs, so this is much larger than the non-long-finality backstop.", DefaultValue = "90000")]
    int LongFinalityMaxReorgDepth { get; set; }

    [ConfigItem(Description = "Maximum number of in-memory base snapshots before conversion to the persisted-snapshot tier kicks in. Counted as `SnapshotCount` of the in-memory repository, not a block-distance depth.", DefaultValue = "128")]
    int MaxInMemoryBaseSnapshotCount { get; set; }

    [ConfigItem(Description = "Maximum size in bytes for a single arena file before a new one is started.", DefaultValue = "1073741824")]
    long ArenaFileSizeBytes { get; set; }

    [ConfigItem(Description = "Estimated-size threshold (bytes) at or above which a persisted-snapshot arena write goes to its own dedicated file instead of being packed into a shared arena.", DefaultValue = "1073741824")]
    long PersistedSnapshotDedicatedArenaThresholdBytes { get; set; }

    [ConfigItem(Description = "Page-cache budget (bytes) for the persisted-snapshot arena. Backs the PageResidencyTracker that drives madvise(DONTNEED) eviction on mmap'd arena files. 0 disables the tracker.", DefaultValue = "4294967296")]
    long PersistedSnapshotArenaPageCacheBytes { get; set; }

    [ConfigItem(Description = "When reclaiming dead persisted-snapshot arena ranges — metadata reservation cleanup and blob-file frontier reset — call fallocate(FALLOC_FL_PUNCH_HOLE) to free the underlying disk blocks. Linux-only; automatically and permanently disabled per arena pool if the filesystem reports the operation unsupported. Set false to skip hole-punching entirely (the page-cache posix_fadvise still runs).", DefaultValue = "true")]
    bool PersistedSnapshotPunchHoleOnReclaim { get; set; }

    [ConfigItem(Description = "Max persisted snapshot compaction size (hierarchical compaction ceiling for persisted layer), in blocks", DefaultValue = "1048576")]
    int PersistedSnapshotMaxCompactSize { get; set; }

    [ConfigItem(Description = "Validate persisted snapshots against in-memory snapshots after conversion (debug/diagnostic only)", DefaultValue = "false")]
    bool ValidatePersistedSnapshot { get; set; }

    [ConfigItem(Description = "Bits per key for the per-snapshot in-memory bloom filter. One unified filter covers address/slot/self-destruct keys plus state-trie and storage-trie node paths. Higher = lower false-positive rate but more RAM. 0 disables the filter (lookups behave as full sweeps).", DefaultValue = "14.0")]
    double PersistedSnapshotBloomBitsPerKey { get; set; }

    [ConfigItem(Description = "Persistent dedicated reader threads used to resolve hinted BAL read sets into the pre-block cache. -1 for 4x logical processor count capped at 64. Values below 1 are clamped to 1. Use --Blocks.ParallelExecutionBatchRead=false to disable BAL warming entirely.", DefaultValue = "-1")]
    int WarmReadConcurrency { get; set; }
}
