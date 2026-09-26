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

    /// <summary>Block number of the EIP-8347 anchor block whose state is converted. Defaults to null.</summary>
    /// <remarks>EIP-8347 leaves the anchor out of the artifacts, so it is supplied out of band. Required to import
    /// external artifacts. For an export, null anchors at the current persisted flat state, and a number ahead of it
    /// runs the node until persistence lands on it. A populated PBT database seeded from another anchor is rejected;
    /// delete it to re-anchor.</remarks>
    [ConfigItem(Description = "Block number of the EIP-8347 anchor block, whose state is converted. Required to import external artifacts. For an export, null anchors at the current persisted flat state, and a number ahead of it runs the node until persistence lands on it.", DefaultValue = "null")]
    long? MigrationAnchor { get; set; }

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

    /// <summary>New directory for the verified EIP-8347 snapshot and preimages; export then exit. Defaults to null.</summary>
    /// <remarks>Requires FlatDb.Enabled and a preimage-flat layout, since deriving EIP-8297 keys needs the
    /// addresses and slot keys that a hash-keyed flat database does not retain.</remarks>
    [ConfigItem(Description = "Export the verified EIP-8347 artifacts for MigrationAnchor from this node's own persisted state into a new directory, then exit. Requires FlatDb.Enabled and a preimage-flat layout.", DefaultValue = "null", HiddenFromDocs = true)]
    string? MigrationExportPath { get; set; }

    /// <summary>Distance from the export anchor, in blocks, at which persistence stops batching. Defaults to 0.</summary>
    /// <remarks>Flat persistence only lands on CompactSize-aligned boundaries, so it would step over an arbitrary
    /// anchor. Within this distance it persists one block at a time instead, and stops on the anchor itself.</remarks>
    [ConfigItem(Description = "Distance from the export anchor, in blocks, within which flat persistence stops batching by FlatDb.CompactSize and persists one block at a time so that it lands exactly on the anchor. 0 uses FlatDb.CompactSize.", DefaultValue = "0", HiddenFromDocs = true)]
    int ExportStepDistance { get; set; }

    /// <summary>Address ranges scanned in parallel when writing the EIP-8347 artifacts. Defaults to 0.</summary>
    /// <remarks>The scan derives a tree key per leaf and hashes every account's code, so it is CPU-bound; the
    /// spools restore the total order the partitioned walk does not have.</remarks>
    [ConfigItem(Description = "Number of parallel workers scanning address ranges of the source state while exporting the EIP-8347 artifacts. 0 uses the processor count.", DefaultValue = "0", HiddenFromDocs = true)]
    int ExportConcurrency { get; set; }

    /// <summary>Export sort budget per scan worker, in bytes, split between the two spools. Defaults to 256 MiB.</summary>
    /// <remarks>Resident sort memory is this times ExportConcurrency, plus up to half as many spare buffers again,
    /// which absorb the sort of a filled buffer while its worker fills the next one. Larger buffers spill fewer,
    /// longer runs and so leave less to merge.</remarks>
    [ConfigItem(Description = "Bytes buffered per export scan worker before the records are sorted and spilled to a temporary run, split between the leaf and preimage spools. Resident sort memory is roughly this times the worker count.", DefaultValue = "268435456", HiddenFromDocs = true)]
    int ExportSortBufferBytes { get; set; }

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

    [ConfigItem(Description = "Minimum number of leaf operations per worker when a wide tree frame splits its buckets across workers and the buckets a worker takes hold less than FoldLargeSubtreeBytes below them: consecutive buckets are merged until they reach it, and a frame that cannot fill two workers folds serially. 0 gives every touched bucket its own worker.", DefaultValue = "128")]
    int FoldMinOperationsPerWorker { get; set; }

    [ConfigItem(Description = "Stored size, in bytes, below the buckets a worker takes from which its share counts as read-bound rather than CPU-bound and is cut at FoldLargeSubtreeMinOperationsPerWorker instead of FoldMinOperationsPerWorker. Each worker's share is measured on its own, so buckets with little stored below them stay CPU-bound however large their siblings are.", DefaultValue = "32768")]
    long FoldLargeSubtreeBytes { get; set; }

    [ConfigItem(Description = "Minimum number of leaf operations per worker when the buckets a worker takes hold at least FoldLargeSubtreeBytes below them.", DefaultValue = "16")]
    int FoldLargeSubtreeMinOperationsPerWorker { get; set; }

    [ConfigItem(Description = "Number of parallel workers copying the source and scanning staged key ranges to derive leaves during the preimage-flat import. 0 uses the processor count. The tree fold runs in a separate single consumer whose zones and wide buckets fold with FoldConcurrency threads.", DefaultValue = "0")]
    int ImportStorageReadConcurrency { get; set; }

    [ConfigItem(Description = "Number of tree leaves buffered per window during the preimage-flat import before it is folded into the tree and committed. 0 uses the built-in default (2000000). Larger windows fold in fewer passes at the cost of memory.", DefaultValue = "0")]
    int ImportWindowSize { get; set; }

    [ConfigItem(Description = "Report persisted PBT record counts, sizes and node-group shape, then exit. Scans ranges within each column in parallel with bounded memory and periodic progress logging, without temporary files or hash, reference and reachability verification.", DefaultValue = "false")]
    bool ScanTree { get; set; }

    [ConfigItem(Description = "Number of parallel workers scanning ranges within each PBT column. 0 uses the processor count.", DefaultValue = "0")]
    int ScanTreeConcurrency { get; set; }

    [ConfigItem(Description = "The persisted node-group key layout: Padded (the group path zero-padded to the column key length, then its nibble count) or Variable (the group path bytes, then 0 for a byte-aligned path or 1 for a nibble-aligned one). Fixed when the pbt database is created; a populated database created with the other layout is rejected.", DefaultValue = "Variable", HiddenFromDocs = true)]
    PbtNodeGroupKeyLayout NodeGroupKeyLayout { get; set; }

    [ConfigItem(Description = "Which prefixless branches without inline leaves are left out of stored node groups and recomputed from their children on read: Interior (relative depths 1-3), OddLevels (relative depths 1 and 3, keeping depth 2) or None (store every node). A database is stamped with the setting when it is created, and the setting must match it afterwards.", DefaultValue = "OddLevels", HiddenFromDocs = true)]
    PbtPrefixlessBranchOmission PrefixlessBranchOmission { get; set; }

    [ConfigItem(Description = "Cache persisted account and storage-run reads across heads, so a new head does not re-read the serving working set from the database. Only the write-set of each persisted batch is dropped.", DefaultValue = "true", HiddenFromDocs = true)]
    bool CarryForwardCache { get; set; }

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
