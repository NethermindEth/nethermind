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

    [ConfigItem(Description = "Capture finalized per-block account/storage changesets into the history columns for archival queries. Off by default; when off the persist path does no extra work.", DefaultValue = "false")]
    bool HistoryEnabled { get; set; }

    [ConfigItem(Description = "Bounded rolling-window retention for flat history, in blocks below the watermark. 0 disables windowing: history is retained unbounded from genesis/pivot, today's shipped behavior.", DefaultValue = "0")]
    ulong HistoryRetentionBlocks { get; set; }

    [ConfigItem(Description = "How many blocks the watermark must advance before an idle history window pruner wakes and re-evaluates the floor. A pruner still owing sweep work paces itself on its pass budget instead. Only consulted when HistoryRetentionBlocks is set.", DefaultValue = "1024")]
    ulong HistoryPruneIntervalBlocks { get; set; }

    [ConfigItem(Description = "Per-pass wall-clock budget, in seconds, for the history window pruner's incremental scan-and-delete. A pass yields at the budget and resumes from its persisted cursor on the next pass rather than running unbounded. Must exceed the longest historical query the node serves: deletes wait for in-flight historical reads, and a read that outlives every pass blocks reclamation until it finishes.", DefaultValue = "5")]
    int HistoryPrunePassBudgetSeconds { get; set; }

    [ConfigItem(Description = "A comma-separated list of contract addresses to retain unbounded (or far deeper than HistoryRetentionBlocks) flat history for, independent of the general rolling window. Static allow-list only - an address is never added or removed except by editing this config and restarting. Both the receipts and the whole block body are retained for every block one of these addresses appears in, so those heights keep their transactions queryable and not just their logs. The cost is body disk: a contract busy enough to match most blocks means most of those bodies are kept, and history pruning stops reclaiming body space over that range. An entry with a retention suffix keeps bodies and receipts only while a height is within that many blocks of the head; a cleanup cursor reclaims them after they fall out. An entry without a retention suffix retains forever and pins the whole clears column and the per-block markers (~40 bytes per block the window never reclaims). Answering below a previously pruned boundary requires History.Pruning to stay enabled: the pruner is what validates, at startup, from which depth each slice's logs are provably retained, and without it those reads fail closed.", DefaultValue = "null")]
    string? HistorySliceAddresses { get; set; }

    [ConfigItem(Description = "Rebuild the state root from flat history rows at every covered block and compare against this node's own headers, once, in the background. Unwindowed archives only. Memory is bounded by FlatDb.HistoryVerifyMaxRows per worker, not by state size.", DefaultValue = "false")]
    bool HistoryVerifyEveryBlock { get; set; }

    [ConfigItem(Description = "Concurrent workers of the every-block history verification. Each worker replays one trie subtree at a time from its own contiguous rows, so workers share nothing but the read-only columns; the count changes memory and wall clock, never the result. 0 means half the processor count.", DefaultValue = "0")]
    int HistoryVerifySegments { get; set; }

    [ConfigItem(Description = "History rows one verification worker holds in memory for the subtree it is replaying. A subtree with more rows is split into its children and a single key with more rows is streamed, so any value works on any archive; larger values mean fewer, bigger subtrees. 0 uses the built-in default of 8 million.", DefaultValue = "0")]
    long HistoryVerifyMaxRows { get; set; }

    [ConfigItem(Description = "Serve eth_getProof at heights below the flat state boundary from the archive commitment columns. Requires an unwindowed (v2) flat history whose commitments cover the height; off by default.", DefaultValue = "false")]
    bool ArchiveProofServeEnabled { get; set; }

    [ConfigItem(Description = "Emit the archive proof commitments: from the tip as blocks are captured, and, with FlatDb.HistoryVerifyEveryBlock, along the every-block walk that retrofits an already-synced archive. A node syncing from genesis needs only the tip capture.", DefaultValue = "false")]
    bool ArchiveProofBuildEnabled { get; set; }

    [ConfigItem(Description = "Concurrent child resolutions inside a single historical proof. Each of a node's 16 children is an independent read, so this is the per-request fan-out; 0 uses the processor count. The number of concurrent proofs is capped by the JSON-RPC module pool, not here.", DefaultValue = "8")]
    int ArchiveProofFanOut { get; set; }

    [ConfigItem(Description = "History rows one historical proof may read before it is refused. A proof that has to scan beyond this is resolving from raw history rather than from commitments, which means the commitment column does not really cover that height. 0 uses the built-in ceiling.", DefaultValue = "0")]
    long ArchiveProofMaxScannedRows { get; set; }

    [ConfigItem(Description = "Checkpoint interval for the archive proof commitments, as a power of two blocks, the same at every trie depth. Smaller means faster cold proofs and more disk. Accepted range 6..12. Changing it invalidates commitments already built. 0 uses the built-in default of 2^9.", DefaultValue = "0")]
    int ArchiveProofCheckpointIntervalLog2 { get; set; }

    [ConfigItem(Description = "Import from pruning trie state db", DefaultValue = "false")]
    bool ImportFromPruningTrieState { get; set; }

    [ConfigItem(Description = "Inline compaction", DefaultValue = "false")]
    bool InlineCompaction { get; set; }

    [ConfigItem(Description = "Flat db layout", DefaultValue = "Flat")]
    FlatLayout Layout { get; set; }

    [ConfigItem(Description = "Max in flight compact job", DefaultValue = "32")]
    int MaxInFlightCompactJob { get; set; }

    [ConfigItem(Description = "Max reorg depth — the force-persist backstop used when EnableLongFinality is off: once the in-memory depth exceeds it while finality is stalled, persistence is forced to bound memory.", DefaultValue = "256")]
    ulong MaxReorgDepth { get; set; }

    [ConfigItem(Description = "Minimum reorg depth", DefaultValue = "128")]
    ulong MinReorgDepth { get; set; }

    [ConfigItem(Description = "Lower bound, in bytes, for the RocksDB write buffer (memtable) size of the flat-state columns. The per-batch adjuster never shrinks a column's memtable below this value. Raising it lets frequent small persistence batches (small CompactSize) coalesce and deduplicate in the memtable instead of churning L0, decoupling write amplification from CompactSize.", DefaultValue = "16777216")]
    long PersistenceWriteBufferFloor { get; set; }

    [ConfigItem(Description = "Regenerate the per-instance compaction offset on startup instead of loading from metadata DB. Use when restoring one backup to multiple instances. Flag is sticky across restarts — toggle off after first restart.", DefaultValue = "false")]
    bool RegenerateCompactionOffset { get; set; }

    [ConfigItem(Description = "Trie cache memory target", DefaultValue = "536870912")]
    ulong TrieCacheMemoryBudget { get; set; }

    [ConfigItem(Description = "Trie warmer worker count (-1 for 3/4 of processor count, 0 to disable)", DefaultValue = "-1")]
    int TrieWarmerWorkerCount { get; set; }

    [ConfigItem(Description = "Verify with trie", DefaultValue = "false")]
    bool VerifyWithTrie { get; set; }

    [ConfigItem(Description = "Enable long finality support with persisted snapshots", DefaultValue = "true")]
    bool EnableLongFinality { get; set; }

    [ConfigItem(Description = "Force-persist backstop used when EnableLongFinality is on, in place of MaxReorgDepth. The persisted-snapshot tier serves deep reorgs, so this is much larger than the non-long-finality backstop.", DefaultValue = "90000")]
    ulong LongFinalityMaxReorgDepth { get; set; }

    [ConfigItem(Description = "Maximum number of in-memory base snapshots before conversion to the persisted-snapshot tier kicks in. Counted as `SnapshotCount` of the in-memory repository, not a block-distance depth. Sized as a ~128 target plus one CompactSize of headroom, since a bulk (CompactSize-wide) conversion drops the in-memory count by up to CompactSize at a boundary — so the tier still retains ~128 base snapshots after each conversion.", DefaultValue = "160")]
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
    ulong PersistedSnapshotMaxCompactSize { get; set; }

    [ConfigItem(Description = "Validate persisted snapshots against in-memory snapshots after conversion (debug/diagnostic only)", DefaultValue = "false")]
    bool ValidatePersistedSnapshot { get; set; }

    [ConfigItem(Description = "Bits per key for the per-snapshot in-memory bloom filter. One unified filter covers address/slot/self-destruct keys plus state-trie and storage-trie node paths. Higher = lower false-positive rate but more RAM. 0 disables the filter (lookups behave as full sweeps).", DefaultValue = "14.0")]
    double PersistedSnapshotBloomBitsPerKey { get; set; }

    [ConfigItem(Description = "Persistent dedicated reader threads used to resolve hinted BAL read sets into the pre-block cache. -1 for 4x logical processor count capped at 64. Values below 1 are clamped to 1. Use --Blocks.ParallelExecutionBatchRead=false to disable BAL warming entirely.", DefaultValue = "-1")]
    int WarmReadConcurrency { get; set; }
}
