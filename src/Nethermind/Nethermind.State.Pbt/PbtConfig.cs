// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public PbtGroupFormat TrieNodeLevels { get; set; } = PbtGroupFormat.Interleaved;
    public PbtTiling TrieNodeTiling { get; set; } = PbtTiling.ClusteredFourLevel;
    public int RootFoldConcurrency { get; set; }

    public string RocksDbOptions { get; set; } =

        // Common across pbt columns. Every key is a hash or a tree path, so there is no locality to
        // exploit and reads are point lookups.
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

        // We bsearch instead of partitioned tree. This take up memory for improved latency.
        "block_based_table_factory.partition_filters=false;" +
        "block_based_table_factory.index_type=kBinarySearch;" +

        "ttl=0;" +
        "periodic_compaction_seconds=0;" +
        "compression=kLZ4Compression;" +

        // Reduce num of files. Tend to be a good thing.
        "target_file_size_multiplier=2;" +

        // Wal flushed manually in persistence.
        "manual_wal_flush=true;" +

        // When an SST is removed, also remove the cached blocks instead of waiting for it to disappear
        "uncache_aggressiveness=1000;" +

        // Small by default, column will override
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

    // Only written when code is deployed, so it is read far more than it is written.
    public string CodeLeavesRocksDbOptions { get; set; } =
        PbtCommonLeafOptions +
        "max_bytes_for_level_base=64000000;" +
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=2;" +
        "";

    // Most of the leaf writes.
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

    // Rewritten from the root down on every block, so small but written the most per byte stored.
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

    // Most writes
    public string StorageTrieNodesRocksDbOptions { get; set; } =
        PbtCommonTrieOptions +
        "max_bytes_for_level_base=350000000;" +
        "write_buffer_size=64000000;" +
        "max_write_buffer_number=8;" +
        "";
}
