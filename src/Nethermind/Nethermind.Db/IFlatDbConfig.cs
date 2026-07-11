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

    [ConfigItem(Description = "Trie warmer worker count (-1 for processor count - 1, 0 to disable)", DefaultValue = "-1")]
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

    [ConfigItem(Description = "Use sparse trie for state root computation instead of PatriciaTree.UpdateRootHash(). " +
        "Computes the root via proof-based incremental hashing while Patricia tree still handles persistence. " +
        "Milestone M2 hybrid mode.", DefaultValue = "false")]
    bool UseSparseRootComputation { get; set; }

    [ConfigItem(Description = "When enabled alongside UseSparseRootComputation, always compute roots via BOTH Patricia and " +
        "sparse trie and compare results every block. Prevents the sparse trie from becoming authoritative — " +
        "Patricia always remains the source of truth for persistence and root hash.", DefaultValue = "false")]
    bool SparseTrieVerificationMode { get; set; }

    [ConfigItem(Description = "M3 mode: when sparse trie is authoritative, skip Patricia BulkSet/Commit entirely. " +
        "Eliminates the hybrid overhead but removes the Patricia fallback if sparse throws. Requires that " +
        "UseSparseRootComputation=true and SparseTrieVerificationMode=false. " +
        "Recommended only after extensive verification-mode validation.", DefaultValue = "false")]
    bool SparseTrieSkipPatricia { get; set; }

    [ConfigItem(Description = "UNSAFE / benchmark-only. Promotes the sparse trie to authoritative from the very " +
        "first block instead of waiting for 10 consecutive shadow-compare matches. This is the only way to " +
        "measure true sparse-only block processing (no Patricia BulkSet/Commit running alongside) without a " +
        "10-block warmup skewing the average. Requires SparseTrieSkipPatricia=true. Never enable in production: " +
        "a sparse bug would corrupt state with no Patricia cross-check.", DefaultValue = "false")]
    bool SparseTrieForceAuthoritative { get; set; }

    [ConfigItem(Description = "M4 (NOT YET WIRED - reserved). Intended to stream per-commit state deltas to a " +
        "background sparse trie task that builds the state root concurrently with EVM execution. The streaming " +
        "task (SparseTrieTask) exists and is root-equivalence-tested, but is deliberately NOT constructed by the " +
        "scope yet (the per-tx WorldState.Commit hook it needs is not built), so this flag currently does nothing. " +
        "Hidden from docs/CLI until the pipeline is fully wired so operators are not offered a no-op switch.",
        DefaultValue = "false", HiddenFromDocs = true, DisabledForCli = true)]
    bool SparseTrieParallelRoot { get; set; }

    [ConfigItem(Description = "When true (default), sparse storage tries replace Patricia's at the per-contract " +
        "level once authoritative. Set to false to keep Patricia storage running even with SkipPatricia=true; " +
        "useful for isolating bugs in the sparse storage path while still benefiting from sparse account " +
        "computation.", DefaultValue = "true")]
    bool SparseTrieAuthoritativeStorage { get; set; }

    [ConfigItem(Description = "Diagnostic-only. When true AND SparseTrieAuthoritativeStorage=true, every " +
        "sparse-computed per-contract storage root is shadow-compared against the same root computed via " +
        "Patricia in parallel. First divergence is logged with full slot-update context for offline analysis. " +
        "Significant CPU cost — use only for bug-hunting.", DefaultValue = "false")]
    bool SparseTrieShadowStorageCompare { get; set; }

    [ConfigItem(Description = "M3 LFU retention: maximum number of HOT accounts kept revealed in the " +
        "preserved sparse trie across blocks. Accounts touched by an update are moved to the top of " +
        "the LFU; the coldest accounts evict back to Blinded entries on commit. Lower values reduce " +
        "memory at the cost of more proof reads next block; higher values keep more of the trie warm. " +
        "Default is int.MaxValue which disables pruning entirely (the preserved trie keeps growing " +
        "until Wipe/Clear). On realblocks workloads cross-block hit rate is low and Prune cost > " +
        "benefit; tune to enable pruning for workloads with hot-account locality, e.g. validator " +
        "set churn or app-specific access patterns.",
        DefaultValue = "2147483647")]
    int SparseTrieMaxHotAccounts { get; set; }

    [ConfigItem(Description = "M3 LFU retention: maximum number of HOT (account, slot) pairs kept " +
        "revealed in the preserved sparse trie across blocks. Same eviction semantics and default " +
        "behaviour as SparseTrieMaxHotAccounts (int.MaxValue = disabled). Tune for storage-heavy " +
        "workloads with hot-slot locality.", DefaultValue = "2147483647")]
    int SparseTrieMaxHotSlots { get; set; }

    [ConfigItem(Description = "Memory bound for the cross-block preserved sparse trie, expressed as the " +
        "maximum number of per-contract storage tries kept revealed across blocks. This is the dimension " +
        "that otherwise grows without bound (one arena per contract ever touched, evicted only on reorg). " +
        "Unlike the LFU caps, pruning here is TRIGGERED, not per-block: a prune runs only on a commit where " +
        "the retained storage-trie count exceeds this value, so warm steady-state operation pays nothing and " +
        "the hot path is never touched. Per-block evict-to-cap was measured to be 7.5x slower on realblocks " +
        "because it discards the working set; triggered pruning avoids that by firing only under genuine " +
        "memory pressure. When a prune fires it collapses cold paths using the LFU caps above (which must be " +
        "set to finite values for the prune to retain anything meaningful). Default int.MaxValue = no memory " +
        "bound (matches historical behaviour). Set to a value sized to your memory budget, e.g. 200000.",
        DefaultValue = "2147483647")]
    int SparseTrieMaxRetainedStorageTries { get; set; }

    [ConfigItem(Description = "Selects which trie warmer implementation runs during execution. " +
        "'Legacy' (default) uses the Patricia-walking TrieWarmer which warms DB pages via real trie traversals. " +
        "'SparseProof' (EXPERIMENTAL) issues sparse-style root-to-leaf proof reads for hint targets and discards the " +
        "result — purpose is DB/OS page-cache warming for paths the sparse trie will hit later. The decoded proof " +
        "nodes are NOT fed back into the sparse trie (that requires the M5 background sparse task), so the CPU spent " +
        "decoding/allocating is pure waste. Useful only as a benchmark probe for the underlying I/O cost. " +
        "'None' disables warming entirely. Only meaningful when UseSparseRootComputation=true.",
        DefaultValue = "Legacy")]
    SparseTrieWarmerVariant SparseTrieWarmer { get; set; }
}

public enum SparseTrieWarmerVariant
{
    Legacy,
    /// <summary>EXPERIMENTAL: full root-to-leaf proof read, result discarded (no sparse-trie reveal).
    /// Until the M5 background sparse task is wired, this only warms DB/OS page cache and burns CPU
    /// on RLP decode that nothing consumes. Don't use as a permanent default.</summary>
    SparseProof,
    /// <summary>Flat-native warmer: on a hint, performs the SAME flat read the EVM will do
    /// (SnapshotBundle slot/account lookup -> persistence -> RocksDB), warming the RocksDB block
    /// cache and the flat read path WITHOUT walking/decoding Patricia nodes. In flat mode the EVM
    /// reads go through SnapshotBundle, not Patricia, so the Legacy warmer's Patricia-node decode
    /// (~59k OwnTime in profiling) is wasted CPU - this variant warms the actual read path instead.</summary>
    Flat,
    None,
}
