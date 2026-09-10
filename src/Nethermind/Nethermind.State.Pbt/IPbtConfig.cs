// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

public interface IPbtConfig : IConfig
{
    [ConfigItem(Description = "Whether to use the experimental EIP-8297 partitioned binary tree state backend. The state root will not match networks using the hexary Patricia trie.", DefaultValue = "false")]
    bool Enabled { get; set; }

    /// <summary>Maximum estimated retained trie-cache memory in bytes; zero disables retention.</summary>
    [ConfigItem(Description = "Memory budget for cached immutable PBT trie node groups, in bytes. Zero disables retention.", DefaultValue = "536870912")]
    ulong TrieCacheMemoryBudget { get; set; }

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

    [ConfigItem(Description = "Number of parallel workers copying the source and scanning staged key ranges to derive leaves during the preimage-flat import. 0 uses the processor count. The tree fold runs in a separate single consumer.", DefaultValue = "0")]
    int ImportStorageReadConcurrency { get; set; }

    [ConfigItem(Description = "Number of tree leaves buffered per window during the preimage-flat import before it is folded into the tree and committed. 0 uses the built-in default (2000000). Larger windows fold in fewer passes at the cost of memory.", DefaultValue = "0")]
    int ImportWindowSize { get; set; }

    [ConfigItem(Description = "Report persisted PBT record counts, sizes and node-group shape, then exit. Scans ranges within each column in parallel with bounded memory and periodic progress logging, without temporary files or hash, reference and reachability verification.", DefaultValue = "false")]
    bool ScanTree { get; set; }

    [ConfigItem(Description = "Number of parallel workers scanning ranges within each PBT column. 0 uses the processor count.", DefaultValue = "0")]
    int ScanTreeConcurrency { get; set; }

    [ConfigItem(Description = "RocksDB options shared by every column of the pbt database. Applied on top of the global database options, and overridden in turn by the per-column options below.", HiddenFromDocs = true)]
    string RocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt metadata column.", HiddenFromDocs = true)]
    string MetadataRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt whole accounts column, keyed by the PBT address hash.", HiddenFromDocs = true)]
    string AccountsRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt whole bytecode column, keyed by code hash.", HiddenFromDocs = true)]
    string CodesRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt storage words column, keyed by complete EIP-8297 storage keys.", HiddenFromDocs = true)]
    string StoragesRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options shared by the pbt account, code and storage node-group columns, keyed by boundary path.", HiddenFromDocs = true)]
    string NodeGroupsRocksDbOptions { get; set; }

    [ConfigItem(Description = "RocksDB options of the pbt content-addressed overflow-code reference records column.", HiddenFromDocs = true)]
    string CodeReferencesRocksDbOptions { get; set; }
}
