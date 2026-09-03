// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

public class PbtConfig : IPbtConfig
{
    public bool Enabled { get; set; }
    public int CompactSize { get; set; } = 32;
    public long CompactionOffset { get; set; } = -1;
    public int MinReorgDepth { get; set; } = 128;
    public int MaxReorgDepth { get; set; } = 256;
    public bool MirrorFlat { get; set; }
    public bool ImportFromPreimageFlat { get; set; }
    public int ImportStorageReadConcurrency { get; set; }
    public int ImportWindowSize { get; set; }
    public bool ScanTree { get; set; }
    public int ScanTreeConcurrency { get; set; }
    public PbtTrieLayout TrieNodeLayout { get; set; } = PbtTrieLayout.FourLevelInterleaved;
    public int RootFoldConcurrency { get; set; }
    public ulong AccountLeafBlobCacheSizeBudget { get; set; } = 64UL.MiB;
    public ulong CodeLeafBlobCacheSizeBudget { get; set; } = 32UL.MiB;
    public ulong StorageLeafBlobCacheSizeBudget { get; set; } = 256UL.MiB;
    public ulong AccountTrieNodeCacheSizeBudget { get; set; } = 128UL.MiB;
    public ulong CodeTrieNodeCacheSizeBudget { get; set; } = 32UL.MiB;
    public ulong StorageTrieNodeCacheSizeBudget { get; set; } = 224UL.MiB;

    public string RocksDbOptions { get; set; } =

        "min_write_buffer_number_to_merge=2;" +
        "block_based_table_factory.block_restart_interval=4;" +
        "block_based_table_factory.data_block_index_type=kDataBlockBinaryAndHash;" +
        "block_based_table_factory.data_block_hash_table_util_ratio=0.7;" +
        "block_based_table_factory.block_size=16000;" +
        "block_based_table_factory.filter_policy=ribbonfilter:10:3;" +
        "max_write_batch_group_size_bytes=4000000;" +
        "block_based_table_factory.pin_l0_filter_and_index_blocks_in_cache=true;" +
        "block_based_table_factory.prepopulate_block_cache=kFlushOnly;" +
        "block_based_table_factory.whole_key_filtering=true;" +
        "level_compaction_dynamic_level_bytes=false;" +

        // Binary-search indexes trade memory for point-lookup latency.
        "block_based_table_factory.partition_filters=false;" +
        "block_based_table_factory.index_type=kBinarySearch;" +

        "ttl=0;" +
        "periodic_compaction_seconds=0;" +
        "compression=kLZ4Compression;" +

        "target_file_size_multiplier=2;" +

        // Persistence flushes the WAL explicitly.
        "manual_wal_flush=true;" +

        "uncache_aggressiveness=1000;" +

        "write_buffer_size=1000000;" +
        "";

    public string MetadataRocksDbOptions { get; set; } = "max_bytes_for_level_base=1000000;";

    // A blob is fetched whole on every stem the fold touches, and a stem absent from the tree is a
    // miss that the last level filter has to answer, so the filters are kept.
    private const string PbtCommonLeafOptions =
        "optimize_filters_for_hits=false;" +
        "target_file_size_base=64000000;" +
        "";

    public string AccountLeavesRocksDbOptions { get; set; } =
        PbtCommonLeafOptions +
        "write_buffer_size=32000000;" +
        "max_write_buffer_number=4;" +
        "";

    // Code is written only on deployment, so this column is read-heavy.
    public string CodeLeavesRocksDbOptions { get; set; } =
        PbtCommonLeafOptions +
        "max_bytes_for_level_base=64000000;" +
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=2;" +
        "";

    public string StorageLeavesRocksDbOptions { get; set; } =
        PbtCommonLeafOptions +
        "max_bytes_for_level_base=350000000;" +
        "write_buffer_size=64000000;" +
        "max_write_buffer_number=8;" +
        "";

    private const string PbtCommonTrieOptions =
        "level_compaction_dynamic_level_bytes=true;" +
        "block_based_table_factory.block_size=16000;" +
        "";

    // Rewritten from the root on every block, so it is write-heavy despite its small size.
    public string AccountTrieNodesRocksDbOptions { get; set; } =
        PbtCommonTrieOptions +
        "write_buffer_size=64000000;" +
        "max_write_buffer_number=4;" +
        "";

    public string CodeTrieNodesRocksDbOptions { get; set; } =
        PbtCommonTrieOptions +
        "max_bytes_for_level_base=64000000;" +
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=2;" +
        "";

    public string StorageTrieNodesRocksDbOptions { get; set; } =
        PbtCommonTrieOptions +
        "max_bytes_for_level_base=350000000;" +
        "write_buffer_size=64000000;" +
        "max_write_buffer_number=8;" +
        "";
}
