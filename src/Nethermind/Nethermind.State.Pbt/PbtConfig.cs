// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

public class PbtConfig : IPbtConfig
{
    public bool Enabled { get; set; }
    public ulong TrieCacheMemoryBudget { get; set; } = 512UL.MiB;
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

    // Missing accounts, storage words and code records need last-level filters too.
    private const string PbtCommonRecordOptions =
        "optimize_filters_for_hits=false;" +
        "target_file_size_base=64000000;" +
        "";

    public string AccountsRocksDbOptions { get; set; } =
        PbtCommonRecordOptions +
        "write_buffer_size=32000000;" +
        "max_write_buffer_number=4;" +
        "";

    // Code is written only on deployment, so this column is read-heavy.
    public string CodesRocksDbOptions { get; set; } =
        PbtCommonRecordOptions +
        "max_bytes_for_level_base=64000000;" +
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=2;" +
        "";

    public string StoragesRocksDbOptions { get; set; } =
        PbtCommonRecordOptions +
        "max_bytes_for_level_base=350000000;" +
        "write_buffer_size=64000000;" +
        "max_write_buffer_number=8;" +
        "";

    public string NodeGroupsRocksDbOptions { get; set; } =
        "level_compaction_dynamic_level_bytes=true;" +
        "block_based_table_factory.block_size=16000;" +
        "max_bytes_for_level_base=350000000;" +
        "write_buffer_size=64000000;" +
        "max_write_buffer_number=8;" +
        "";

    public string CodeReferencesRocksDbOptions { get; set; } =
        PbtCommonRecordOptions +
        "max_bytes_for_level_base=64000000;" +
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=2;" +
        "";
}
