// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

public interface IFlatDbConfig : IConfig
{
    [ConfigItem(Description = "Block cache size budget", DefaultValue = "1073741824")]
    long BlockCacheSizeBudget { get; set; }

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

    [ConfigItem(Description = "Max reorg depth", DefaultValue = "256")]
    int MaxReorgDepth { get; set; }

    [ConfigItem(Description = "Minimum compact size (power of 2, floor for hierarchical compaction)", DefaultValue = "2")]
    int MinCompactSize { get; set; }

    [ConfigItem(Description = "Minimum reorg depth", DefaultValue = "128")]
    int MinReorgDepth { get; set; }

    [ConfigItem(Description = "Trie cache memory target", DefaultValue = "536870912")]
    long TrieCacheMemoryBudget { get; set; }

    [ConfigItem(Description = "Trie warmer worker count (-1 for processor count - 1, 0 to disable)", DefaultValue = "-1")]
    int TrieWarmerWorkerCount { get; set; }

    [ConfigItem(Description = "Verify with trie", DefaultValue = "false")]
    bool VerifyWithTrie { get; set; }

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
    None,
}
