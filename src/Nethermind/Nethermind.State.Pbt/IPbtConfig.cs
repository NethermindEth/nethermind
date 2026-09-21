// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

public interface IPbtConfig : IConfig
{
    [ConfigItem(Description = "Whether to use the experimental EIP-8297 partitioned binary tree state backend. With a binaryTrieTime after genesis in the chain specification the node migrates from the flat state at activation (EIP-8347); otherwise the binary tree is used from genesis and the state root will not match networks using the hexary Patricia trie.", DefaultValue = "false")]
    bool Enabled { get; set; }

    /// <summary>Path to the migration anchor manifest; null for genesis bootstrap or an already seeded PBT database. Defaults to null.</summary>
    [ConfigItem(Description = "Path to the migration anchor manifest; null for genesis bootstrap or an already seeded PBT database. A populated PBT database imported from another source is rejected; delete it to re-anchor.", DefaultValue = "null")]
    string? MigrationManifestPath { get; set; }

    /// <summary>Path to the canonical EIP-8347 PBT snapshot; paired with MigrationPreimagesPath. Defaults to null.</summary>
    [ConfigItem(Description = "Path to the canonical EIP-8347 PBT snapshot; paired with MigrationPreimagesPath.", DefaultValue = "null")]
    string? MigrationSnapshotPath { get; set; }

    /// <summary>Path to canonical EIP-8347 preimages; paired with MigrationSnapshotPath. Defaults to null.</summary>
    [ConfigItem(Description = "Path to canonical EIP-8347 preimages; paired with MigrationSnapshotPath.", DefaultValue = "null")]
    string? MigrationPreimagesPath { get; set; }

    /// <summary>Path to a separate offline read-only preimage-flat source database. Defaults to null.</summary>
    [ConfigItem(Description = "Path to a separate offline read-only preimage-flat source database.", DefaultValue = "null")]
    string? MigrationPreimageSourcePath { get; set; }

    /// <summary>Whether to generate a separate offline source from the MPT genesis allocation. Defaults to false.</summary>
    [ConfigItem(Description = "Whether to generate a separate offline source from the MPT genesis allocation.", DefaultValue = "false")]
    bool MigrationGenesisBootstrap { get; set; }

    /// <summary>New directory for a verified offline snapshot, preimages and manifest; export then exit. Defaults to null.</summary>
    [ConfigItem(Description = "Export verified portable artifacts from genesis bootstrap or an offline preimage source into a new directory, then exit before networking. Requires a scheduled binaryTrieTime.", DefaultValue = "null", HiddenFromDocs = true)]
    string? MigrationExportPath { get; set; }

    /// <summary>Whether to report the known child header's state root instead of the computed PBT root. Defaults to false.</summary>
    [ConfigItem(Description = "Report the known child header's state root instead of the computed PBT root. Diagnostic use only: this bypasses independent state-root verification against the header while still computing and retaining the PBT root. Does not affect flat mirror mode.", DefaultValue = "false")]
    bool FakeMatchingStateRoot { get; set; }

    /// <summary>Maximum estimated retained account trie-cache memory in bytes; zero disables this partition.</summary>
    [ConfigItem(Description = "Memory budget for cached account PBT trie node groups, in bytes. Zero disables this cache partition.", DefaultValue = "134217728")]
    ulong AccountTrieNodeCacheSizeBudget { get; set; }

    /// <summary>Maximum estimated retained code trie-cache memory in bytes; zero disables this partition.</summary>
    [ConfigItem(Description = "Memory budget for cached code PBT trie node groups, in bytes. Zero disables this cache partition.", DefaultValue = "33554432")]
    ulong CodeTrieNodeCacheSizeBudget { get; set; }

    /// <summary>Maximum estimated retained storage trie-cache memory in bytes; zero disables this partition.</summary>
    [ConfigItem(Description = "Memory budget for cached storage PBT trie node groups, in bytes. Zero disables this cache partition.", DefaultValue = "234881024")]
    ulong StorageTrieNodeCacheSizeBudget { get; set; }

    [ConfigItem(Description = "Block cache size budget for the PBT account and storage record columns, in bytes.", DefaultValue = "1073741824")]
    ulong BlockCacheSizeBudget { get; set; }

    [ConfigItem(Description = "The number of in-memory snapshots (one per block) merged into a single snapshot by compaction, and the persist batch granularity, in blocks.", DefaultValue = "32")]
    int CompactSize { get; set; }

    [ConfigItem(Description = "Shifts the compaction and persistence boundaries so nodes do not all compact on the same blocks. Negative generates one per node on first run and stores it; any other value is used as-is, in blocks.", DefaultValue = "-1")]
    long CompactionOffset { get; set; }

    [ConfigItem(Description = "The minimum depth, in blocks, that a state must be below the head before it may be persisted to disk.", DefaultValue = "128")]
    int MinReorgDepth { get; set; }

    [ConfigItem(Description = "The depth, in blocks, past which states are force-persisted even without finality, bounding memory use, in blocks.", DefaultValue = "256")]
    int MaxReorgDepth { get; set; }

    [ConfigItem(Description = "Run the PBT backend as a shadow of the flat backend rather than as the state backend: every main block processing read is compared against the flat one and every write is applied to both, and PBT persists exactly the ranges the flat db persists. Requires FlatDb.Enabled, and requires both databases to already hold the very same persisted state - from an empty data directory, or after an ImportFromPreimageFlat run. Diagnostic use only; it roughly doubles the cost of state access, and `Blocks.PreWarming` should be `None` so reads are not served from the pre-block caches before they reach the mirror.", DefaultValue = "false")]
    bool MirrorFlat { get; set; }

    [ConfigItem(Description = "Rebuild the PBT state from an existing preimage-flat state database, then exit. Requires a fully synced FlatLayout.PreimageFlat 'flat' database (and the 'code' database) in the data directory.", DefaultValue = "false")]
    bool ImportFromPreimageFlat { get; set; }

    [ConfigItem(Description = "Maximum number of threads, including the calling one, folding the key zones and their wide buckets at once when computing the tree root. 0 uses the processor count; 1 folds serially.", DefaultValue = "0")]
    int FoldConcurrency { get; set; }

    [ConfigItem(Description = "Minimum number of leaf operations per worker when a wide tree frame whose stored subtree is smaller than FoldLargeSubtreeBytes splits its buckets across workers: consecutive buckets are merged until they reach it, and a frame that cannot fill two workers folds serially. 0 gives every touched bucket its own worker.", DefaultValue = "128")]
    int FoldMinOperationsPerWorker { get; set; }

    [ConfigItem(Description = "Stored subtree size, in bytes, from which a tree frame counts as read-bound rather than CPU-bound and splits its buckets across workers at FoldLargeSubtreeMinOperationsPerWorker instead of FoldMinOperationsPerWorker.", DefaultValue = "32768")]
    long FoldLargeSubtreeBytes { get; set; }

    [ConfigItem(Description = "Minimum number of leaf operations per worker when a tree frame whose stored subtree is at least FoldLargeSubtreeBytes splits its buckets across workers.", DefaultValue = "16")]
    int FoldLargeSubtreeMinOperationsPerWorker { get; set; }

    [ConfigItem(Description = "Number of parallel workers copying the source and scanning staged key ranges to derive leaves during the preimage-flat import. 0 uses the processor count. The tree fold runs in a separate single consumer whose zones and wide buckets fold with FoldConcurrency threads.", DefaultValue = "0")]
    int ImportStorageReadConcurrency { get; set; }

    [ConfigItem(Description = "Number of tree leaves buffered per window during the preimage-flat import before it is folded into the tree and committed. 0 uses the built-in default (2000000). Larger windows fold in fewer passes at the cost of memory.", DefaultValue = "0")]
    int ImportWindowSize { get; set; }

    [ConfigItem(Description = "Report persisted PBT record counts, sizes and node-group shape, then exit. Scans ranges within each column in parallel with bounded memory and periodic progress logging, without temporary files or hash, reference and reachability verification.", DefaultValue = "false")]
    bool ScanTree { get; set; }

    [ConfigItem(Description = "Number of parallel workers scanning ranges within each PBT column. 0 uses the processor count.", DefaultValue = "0")]
    int ScanTreeConcurrency { get; set; }

    [ConfigItem(Description = "The persisted node-group key layout: Padded (the group path zero-padded to the column key length, then its nibble count) or Variable (the group path bytes, then 0 for a byte-aligned path or 1 for a nibble-aligned one). Fixed when the pbt database is created; a populated database created with the other layout is rejected.", DefaultValue = "Padded", HiddenFromDocs = true)]
    PbtNodeGroupKeyLayout NodeGroupKeyLayout { get; set; }

    [ConfigItem(Description = "Which prefixless branches without inline leaves are left out of stored node groups and recomputed from their children on read: Interior (relative depths 1-3), OddLevels (relative depths 1 and 3, keeping depth 2) or None (store every node). All layouts are readable, so the setting can change on an existing database; groups convert as they are rewritten.", DefaultValue = "Interior", HiddenFromDocs = true)]
    PbtPrefixlessBranchOmission PrefixlessBranchOmission { get; set; }

    [ConfigItem(Description = "Keep node-group payloads in slab-allocated native memory sized to jemalloc-style classes. Off rents pooled managed arrays in power-of-two buckets instead.", DefaultValue = "true", HiddenFromDocs = true)]
    bool NativeNodeGroupMemory { get; set; }

    [ConfigItem(Description = "RocksDB options shared by every column of the pbt database. Applied on top of the global database options, and overridden in turn by the per-column options below.", HiddenFromDocs = true)]
    string RocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt metadata column.", HiddenFromDocs = true)]
    string MetadataRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt whole accounts column, keyed by the PBT address hash.", HiddenFromDocs = true)]
    string AccountsRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt whole bytecode column, keyed by code hash.", HiddenFromDocs = true)]
    string CodesRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt storage words column, keyed by the address hash, zone and remaining bytes of the EIP-8297 storage key.", HiddenFromDocs = true)]
    string StoragesRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options shared by the pbt account, code and storage node-group columns, keyed by boundary path.", HiddenFromDocs = true)]
    string NodeGroupsRocksDbOptions { get; set; }
}
