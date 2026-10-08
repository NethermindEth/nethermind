// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Pbt;

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

    /// <summary>Path to the canonical EIP-8347 PBT snapshot, the only source the PBT state is built from. Defaults to null.</summary>
    /// <remarks>Without MigrationPreimagesPath the snapshot is trusted as is: nothing ties it to the anchor's MPT state root.</remarks>
    [ConfigItem(Description = "Path to the canonical EIP-8347 PBT snapshot. The PBT state is built from the snapshot alone; with MigrationPreimagesPath it is also verified against the anchor's MPT state root.", DefaultValue = "null")]
    string? MigrationSnapshotPath { get; set; }

    /// <summary>Path to canonical EIP-8347 preimages verifying MigrationSnapshotPath against the anchor's MPT state root. Defaults to null.</summary>
    [ConfigItem(Description = "Path to canonical EIP-8347 preimages. They only verify the imported MigrationSnapshotPath against the anchor's MPT state root, so they require it.", DefaultValue = "null")]
    string? MigrationPreimagesPath { get; set; }

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

    /// <summary>Address ranges scanned in parallel when writing the EIP-8347 artifacts or the preimage-flat import's leaf spool. Defaults to 0.</summary>
    /// <remarks>The scan derives a tree key per leaf and hashes every account's code, so it is CPU-bound; the
    /// spools restore the total order the partitioned walk does not have.</remarks>
    [ConfigItem(Description = "Number of parallel workers scanning address ranges of the source state while exporting the EIP-8347 artifacts or importing from a preimage-flat database. 0 uses the processor count.", DefaultValue = "0", HiddenFromDocs = true)]
    int ExportConcurrency { get; set; }

    /// <summary>Export and preimage-flat import sort budget per scan worker, in bytes, split between the spools. Defaults to 256 MiB.</summary>
    /// <remarks>Resident sort memory is this times ExportConcurrency, plus up to half as many spare buffers again,
    /// which absorb the sort of a filled buffer while its worker fills the next one. Larger buffers spill fewer,
    /// longer runs and so leave less to merge.</remarks>
    [ConfigItem(Description = "Bytes buffered per export or preimage-flat import scan worker before the records are sorted and spilled to a temporary run, split between the leaf and preimage spools. Resident sort memory is roughly this times the worker count.", DefaultValue = "268435456", HiddenFromDocs = true)]
    int ExportSortBufferBytes { get; set; }

    /// <summary>Whether the export also writes the EIP-8347 preimage stream beside the snapshot. Defaults to true.</summary>
    [ConfigItem(Description = "Whether the EIP-8347 export also writes preimages.bin. When false, only snapshot.pbt is written.", DefaultValue = "true", HiddenFromDocs = true)]
    bool ExportPreimages { get; set; }

    /// <summary>Memory for the preimage verifier's bucket tables, in bytes, shared by all its workers. Defaults to 4 GiB.</summary>
    /// <remarks>The preimage reader pauses while the tables are full; the workers sweep them once they are 90% full,
    /// or once the reader is done.</remarks>
    [ConfigItem(Description = "Bytes of listed accounts and slots the EIP-8347 preimage verifier queues in its bucket tables, shared by all its workers, before the preimage reader pauses.", DefaultValue = "4294967296", HiddenFromDocs = true)]
    long MigrationVerifyBucketBytes { get; set; }

    /// <summary>Threads each parallel stage of the background EIP-8347 migration import may use. Defaults to 1.</summary>
    /// <remarks>The import runs alongside block processing, so it is kept narrow unless raised.</remarks>
    [ConfigItem(Description = "Maximum number of threads each parallel stage of the background EIP-8347 migration import (offline export scan, staging writes, preimage verification, tree fold) uses, leaving room for block processing. 0 uses the processor count.", DefaultValue = "1")]
    int ImportConcurrency { get; set; }

    /// <summary>Whether to report the known child header's state root instead of the computed PBT root. Defaults to false.</summary>
    [ConfigItem(Description = "Report the known child header's state root instead of the computed PBT root. Diagnostic use only: this bypasses independent state-root verification against the header while still computing and retaining the PBT root.", DefaultValue = "false")]
    bool FakeMatchingStateRoot { get; set; }

    /// <summary>Whether to import the EIP-8347 snapshot at MigrationAnchor and keep running on PBT from it. Defaults to false.</summary>
    [ConfigItem(Description = "Import the EIP-8347 snapshot from MigrationSnapshotPath and MigrationPreimagesPath at MigrationAnchor as the PBT state before block processing, then keep running on PBT with FakeMatchingStateRoot implied. A restart with the same snapshot reuses the import. Diagnostic use only; not available with a scheduled binaryTrieTime.", DefaultValue = "false", HiddenFromDocs = true)]
    bool ImportMigrationSnapshotWithFakeRoots { get; set; }

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

    [ConfigItem(Description = "Number of tree leaves buffered per window during the preimage-flat import before it is folded into the tree and committed. 0 uses the built-in default (2000000). Larger windows fold in fewer passes at the cost of memory.", DefaultValue = "0")]
    int ImportWindowSize { get; set; }

    [ConfigItem(Description = "Report persisted PBT record counts, sizes and node-group shape, then exit. Scans ranges within each column in parallel with bounded memory and periodic progress logging, without temporary files or hash, reference and reachability verification.", DefaultValue = "false")]
    bool ScanTree { get; set; }

    [ConfigItem(Description = "Number of parallel workers scanning ranges within each PBT column. 0 uses the processor count.", DefaultValue = "0")]
    int ScanTreeConcurrency { get; set; }

    [ConfigItem(Description = "Cache persisted account and storage-run reads across heads, so a new head does not re-read the serving working set from the database. Only the write-set of each persisted batch is dropped.", DefaultValue = "true", HiddenFromDocs = true)]
    bool CarryForwardCache { get; set; }

    [ConfigItem(Description = "Keep node-group payloads in slab-allocated native memory sized to jemalloc-style classes. Off rents pooled managed arrays in power-of-two buckets instead.", DefaultValue = "true", HiddenFromDocs = true)]
    bool NativeNodeGroupMemory { get; set; }

    [ConfigItem(Description = "Write node groups (TopNodeGroups, AccountNodeGroups, CodeNodeGroups and StorageNodeGroups; the root group stays direct) through an append-only trie node log (with an in-memory index) that is merged into RocksDB one generation at a time, so groups rewritten within a generation are written to RocksDB once. Experimental: ScanTree and node-group key enumeration read RocksDB only, so they miss groups still in the log unless it was drained (TrieNodeLogDrainOnShutdown).", DefaultValue = "false")]
    bool TrieNodeLogEnabled { get; set; }

    [ConfigItem(Description = "Byte budget of the account partition of the trie node log (TopNodeGroups, AccountNodeGroups and CodeNodeGroups), split evenly over its shards: each shard's generation, i.e. its deduplication window before it is merged into RocksDB, is this divided by the partition's shard count. Each live generation also keeps an in-memory index of 1/64 of its size (1/32 for the storage partition).", DefaultValue = "524288000")]
    long TrieNodeLogAccountBytes { get; set; }

    [ConfigItem(Description = "Byte budget of the storage partition of the trie node log (StorageNodeGroups), split evenly over its shards like TrieNodeLogAccountBytes.", DefaultValue = "524288000")]
    long TrieNodeLogStorageBytes { get; set; }

    [ConfigItem(Description = "Number of shards of the account partition of the trie node log, a power of two up to 256. Groups are sharded by the hash of their key; shards are appended to and merged in parallel.", DefaultValue = "2")]
    int TrieNodeLogAccountShardCount { get; set; }

    [ConfigItem(Description = "Number of shards of the storage partition of the trie node log, a power of two up to 256.", DefaultValue = "2")]
    int TrieNodeLogStorageShardCount { get; set; }

    [ConfigItem(Description = "Maximum number of trie node log generation merges (across all shards) running at the same time; each merge is one RocksDB write batch.", DefaultValue = "2")]
    int TrieNodeLogMaxConcurrentMerges { get; set; }

    [ConfigItem(Description = "Sealed trie node log generations a shard may accumulate beyond TrieNodeLogMergeLag before starting a new generation waits for a merge to finish, which stalls persistence until the merges catch up.", DefaultValue = "2")]
    int TrieNodeLogMergeBacklogMargin { get; set; }

    [ConfigItem(Description = "Merge the whole trie node log into RocksDB at shutdown. The log otherwise persists across restarts, keeping its deduplication window; mainly for debugging.", DefaultValue = "false")]
    bool TrieNodeLogDrainOnShutdown { get; set; }

    [ConfigItem(Description = "Number of sealed trie node log generations kept unmerged behind the newest one. A generation is merged into RocksDB only once this many newer generations are sealed, and keys rewritten in those are skipped, so the effective deduplication window is (1 + this) generations at the cost of that many extra generation files and in-memory indexes.", DefaultValue = "1")]
    int TrieNodeLogMergeLag { get; set; }

    [ConfigItem(Description = "TrieNodeLogMergeLag of the second-level trie node log, or -1 to disable it. Each merged trie node log generation is copied, deduplicated, into the second-level log instead of RocksDB; it has the same size and backlog settings as the first, an in-memory index of 1/16 of its size for both partitions, and merges its own generations into RocksDB once this many newer ones are sealed. The surviving groups are less likely to be rewritten, and the log's index answers their reads faster than RocksDB.", DefaultValue = "1")]
    int TrieNodeLogSecondLevelMergeLag { get; set; }

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
