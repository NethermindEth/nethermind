// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Db.Rocks.Config;

public class DbConfig : IDbConfig
{
    public static DbConfig Default = new();

    public ulong SharedBlockCacheSize { get; set; } = 256UL.MiB;
    public bool SkipMemoryHintSetting { get; set; } = false;

    public bool WriteAheadLogSync { get; set; } = false;
    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;

    public int? MaxOpenFiles { get; set; }
    public bool? SkipCheckingSstFileSizesOnDbOpen { get; set; }
    public ulong? ReadAheadSize { get; set; } = 256UL.KiB;

    public string RocksDbOptions { get; set; } =

        // This section affect the write buffer, or memtable. Note, the size of write buffer affect the size of l0
        // file which affect compactions. The options here does not effect how the sst files are read... probably.
        // But read does go through the write buffer first, before going through the row cache (or is it before memtable?)
        // block cache and then finally the LSM/SST files.
        "min_write_buffer_number_to_merge=1;" +
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=2;" +
        "memtable_whole_key_filtering=true;" +
        "memtable_prefix_bloom_size_ratio=0.02;" +

        // Rocksdb turned this on by default a few releases ago, but we don't want it yet; the impact on reads is unclear
        // significant or not.
        "level_compaction_dynamic_level_bytes=false;" +

        // Default is 1.6GB.
        // Increase it to reduce stalls under heavy compaction.
        "max_compaction_bytes=4000000000;" +

        "compression=kSnappyCompression;" +
        "optimize_filters_for_hits=true;" +
        "advise_random_on_open=true;" +

        // Target size of each SST file. Increase to reduce number of file. Default is 64MB.
        "target_file_size_base=64000000;" +

        // The first level size. Should be WriteBufferSize * WriteBufferNumber or you'll have higher write amp,
        // but lowering this to match write buffer will make the LSM have more level, so you'll have more read amp.
        "max_bytes_for_level_base=256000000;" +

        // Note, this is before compression. On disk size may be lower. The on disk size is the minimum amount of read
        // each io will do. On most SSD, the minimum read size is 4096 byte. So don't set it to lower than that, unless
        // you have an optane drive or some kind of RAM disk. Lower block size also means bigger index size.
        "block_based_table_factory.block_size=16000;" +

        // No significant downside. Just set it.
        "block_based_table_factory.pin_l0_filter_and_index_blocks_in_cache=true;" +

        // Make the index in cache have higher priority, so it is kept more in cache.
        "block_based_table_factory.cache_index_and_filter_blocks_with_high_priority=true;" +

        "block_based_table_factory.format_version=5;" +

        // Two level index split the index into two level. First index point to second level index, which actually
        // point to the block, which get binary searched to the value. This means potentially two iop instead of one per
        // read, and probably more processing overhead. But it significantly reduces memory usage and make block
        // processing time more consistent. So its enabled by default. That said, if you got the RAM, maybe disable
        // this.
        // See https://rocksdb.org/blog/2017/05/12/partitioned-index-filter.html
        "block_based_table_factory.index_type=kTwoLevelIndexSearch;" +
        "block_based_table_factory.partition_filters=true;" +
        "block_based_table_factory.metadata_block_size=4096;" +

        "block_based_table_factory.filter_policy=bloomfilter:10;" +
        "";
    public string? AdditionalRocksDbOptions { get; set; }

    public bool? VerifyChecksum { get; set; } = true;
    public bool EnableFileWarmer { get; set; } = false;
    public double CompressibilityHint { get; set; } = 1.0;
    public FlushOnExitMode FlushOnExit { get; set; } = FlushOnExitMode.WalOnly;

    public string BadBlocksDbRocksDbOptions { get; set; } = "";
    public string? BadBlocksDbAdditionalRocksDbOptions { get; set; }

    public string BlockAccessListsDbRocksDbOptions { get; set; } = "";
    public string? BlockAccessListsDbAdditionalRocksDbOptions { get; set; }

    public string BlobTransactionsDbRocksDbOptions { get; set; } =
        "block_based_table_factory.block_cache=32000000;";
    public string? BlobTransactionsDbAdditionalRocksDbOptions { get; set; }
    public string BlobTransactionsFullBlobTxsDbRocksDbOptions { get; set; } = "";
    public string? BlobTransactionsFullBlobTxsDbAdditionalRocksDbOptions { get; set; }
    public string BlobTransactionsLightBlobTxsDbRocksDbOptions { get; set; } = "";
    public string? BlobTransactionsLightBlobTxsDbAdditionalRocksDbOptions { get; set; }
    public string BlobTransactionsProcessedTxsDbRocksDbOptions { get; set; } = "";
    public string? BlobTransactionsProcessedTxsDbAdditionalRocksDbOptions { get; set; }


    public double ReceiptsDbCompressibilityHint { get; set; } = 0.35;
    public string ReceiptsDbRocksDbOptions { get; set; } =
        "write_buffer_size=2000000;" +
        "block_based_table_factory.block_cache=8000000;" +
        "optimize_filters_for_hits=false;";
    public string? ReceiptsDbAdditionalRocksDbOptions { get; set; } = "";

    public string ReceiptsDefaultDbRocksDbOptions { get; set; } = "";
    public string? ReceiptsDefaultDbAdditionalRocksDbOptions { get; set; }
    public string ReceiptsTransactionsDbRocksDbOptions { get; set; } = "";
    public string? ReceiptsTransactionsDbAdditionalRocksDbOptions { get; set; }

    public string ReceiptsBlocksDbRocksDbOptions { get; set; } =
        "compaction_pri=kOldestLargestSeqFirst;" +
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=4;";
    public string? ReceiptsBlocksDbAdditionalRocksDbOptions { get; set; }

    public string BlocksDbRocksDbOptions { get; set; } =
        "write_buffer_size=64000000;" +
        "block_based_table_factory.block_cache=32000000;" +
        "compaction_pri=kOldestLargestSeqFirst;" +
        "optimize_filters_for_hits=false;";
    public string? BlocksDbAdditionalRocksDbOptions { get; set; } = "";

    public string HeadersDbRocksDbOptions { get; set; } =
        "write_buffer_size=8000000;" +
        "block_based_table_factory.block_cache=32000000;" +
        "compaction_pri=kOldestLargestSeqFirst;" +
        "optimize_filters_for_hits=false;" +
        "block_based_table_factory.block_size=32000;" +
        "max_bytes_for_level_base=128000000;" +
        "";
    public string? HeadersDbAdditionalRocksDbOptions { get; set; } = "";

    public ulong? BlockNumbersDbRowCacheSize { get; set; } = 16UL.MiB;
    public string BlockNumbersDbRocksDbOptions { get; set; } =
        "write_buffer_size=8000000;" +
        "max_bytes_for_level_base=16000000;" +
        "block_based_table_factory.block_cache=16000000;" +
        "block_based_table_factory.block_size=4096;" +
        "optimize_filters_for_hits=false;" +
        "memtable=prefix_hash:1000000;" +
        "allow_concurrent_memtable_write=false;" +
        "";
    public string? BlockNumbersDbAdditionalRocksDbOptions { get; set; } = "";

    public string BlockInfosDbRocksDbOptions { get; set; } =
        "write_buffer_size=4000000;" +
        "max_bytes_for_level_base=32000000;" +
        "optimize_filters_for_hits=false;" +
        "block_based_table_factory.block_cache=16000000;" +
        "block_based_table_factory.block_size=32000;" +
        "compaction_pri=kOldestLargestSeqFirst;";
    public string? BlockInfosDbAdditionalRocksDbOptions { get; set; } = "";

    public string PendingTxsDbRocksDbOptions { get; set; } =
        "write_buffer_size=4000000;";
    public string? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    public ulong? CodeDbRowCacheSize { get; set; } = 16UL.MiB;
    public string CodeDbRocksDbOptions { get; set; } =
        "write_buffer_size=16000000;" +
        "block_based_table_factory.block_cache=16000000;" +
        "optimize_filters_for_hits=false;" +
        "prefix_extractor=capped:8;" +
        "block_based_table_factory.index_type=kHashSearch;" +
        "block_based_table_factory.block_size=4096;" +
        "memtable=prefix_hash:1000000;" +
        // Bloom crash with kHashSearch index
        "block_based_table_factory.filter_policy=null;" +
        "allow_concurrent_memtable_write=false;";
    public string? CodeDbAdditionalRocksDbOptions { get; set; }

    public string MetadataDbRocksDbOptions { get; set; } =
        "write_buffer_size=1000000;" +
        "max_bytes_for_level_base=16000000;";
    public string? MetadataDbAdditionalRocksDbOptions { get; set; }

    public string L1OriginDbRocksDbOptions { get; set; } = "";

    public string? L1OriginDbAdditionalRocksDbOptions { get; set; }

    public string LogIndexStorageDbRocksDbOptions { get; set; } = "";
    public string LogIndexStorageDbAdditionalRocksDbOptions { get; set; } = "";
    public string LogIndexStorageMetaDbRocksDbOptions { get; set; } = "";
    public string LogIndexStorageMetaDbAdditionalRocksDbOptions { get; set; } = "";
    public string LogIndexStorageAddressesDbRocksDbOptions { get; set; } = "";
    public string LogIndexStorageAddressesDbAdditionalRocksDbOptions { get; set; } = "";
    public string LogIndexStorageTopics0DbRocksDbOptions { get; set; } = "";
    public string LogIndexStorageTopics0DbAdditionalRocksDbOptions { get; set; } = "";
    public string LogIndexStorageTopics1DbRocksDbOptions { get; set; } = "";
    public string LogIndexStorageTopics1DbAdditionalRocksDbOptions { get; set; } = "";
    public string LogIndexStorageTopics2DbRocksDbOptions { get; set; } = "";
    public string LogIndexStorageTopics2DbAdditionalRocksDbOptions { get; set; } = "";
    public string LogIndexStorageTopics3DbRocksDbOptions { get; set; } = "";
    public string LogIndexStorageTopics3DbAdditionalRocksDbOptions { get; set; } = "";

    public bool? FlatDbVerifyChecksum { get; set; } = true;
    public string FlatDbRocksDbOptions { get; set; } =

        // Common across flat columns.
        "min_write_buffer_number_to_merge=2;" +
        "block_based_table_factory.block_restart_interval=4;" +
        "block_based_table_factory.data_block_index_type=kDataBlockBinaryAndHash;" +
        "block_based_table_factory.data_block_hash_table_util_ratio=0.7;" +
        "block_based_table_factory.block_size=16000;" +
        "block_based_table_factory.filter_policy=ribbonfilter:10:3;" +
        "max_write_batch_group_size_bytes=4000000;" +
        "block_based_table_factory.pin_l0_filter_and_index_blocks_in_cache=true;" +
        "block_based_table_factory.prepopulate_block_cache=kFlushOnly;" +
        "block_based_table_factory.whole_key_filtering=true;" + // should be default. Just in case.
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
    public string? FlatDbAdditionalRocksDbOptions { get; set; }

    public string? FlatMetadataDbRocksDbOptions { get; set; } = "max_bytes_for_level_base=1000000;";
    public string? FlatMetadataDbAdditionalRocksDbOptions { get; set; }

    // Account is too small so we make it so that the file and buffer is smaller so that it does not compact too much
    // at once
    public string? FlatAccountDbRocksDbOptions { get; set; } =
        // The account db is small, already using slim encoding. Disabling compression does not lose much.
        "compression=kNoCompression;" +

        // Keep last level bloom filter. Take up most index memory
        "optimize_filters_for_hits=false;" +

        // account db is really small in writes, so we set low buffer size to prevent too many different version account
        // in the same memtable.
        "target_file_size_multiplier=3;" +
        "target_file_size_base=32000000;" +
        "max_bytes_for_level_multiplier=15;" + // Reduce level count
        "max_bytes_for_level_base=128000000;" +

        // account db have no benefit in locality whatsoever, and have compression disabled.
        "block_based_table_factory.block_size=4096;" +

        // Smaller
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=4;" +
        "";
    public string? FlatAccountDbAdditionalRocksDbOptions { get; set; }

    public string? FlatStorageDbRocksDbOptions { get; set; } =
        // Keep last level bloom filter. Take up most index memory
        "optimize_filters_for_hits=false;" +

        // Much like account kinda small.
        "target_file_size_base=64000000;" +

        // Using 4kb size is faster, IO wise, but uses additional 500 MB of memory, which if put on block cache is much better.
        "block_based_table_factory.block_size=8000;" +

        // Smaller
        "write_buffer_size=32000000;" +
        "max_write_buffer_number=4;" +
        "";

    public string? FlatStorageDbAdditionalRocksDbOptions { get; set; }

    const string? FlatDbCommonTrieOptions =
        "level_compaction_dynamic_level_bytes=true;" +
        "block_based_table_factory.block_restart_interval=8;" +
        "block_based_table_factory.block_size=16000;" +
        "";

    // Only 1 gig in total, but almost 1/3rd of the writes.
    public string? FlatStateTopNodesDbRocksDbOptions { get; set; } =
        FlatDbCommonTrieOptions +
        "write_buffer_size=64000000;" +
        "max_write_buffer_number=4;" +
        "";
    public string? FlatStateNodesDbAdditionalRocksDbOptions { get; set; }

    // So not written as much so lower buffer size
    public string? FlatStateNodesDbRocksDbOptions { get; set; } =
        FlatDbCommonTrieOptions +
        "write_buffer_size=32000000;" +
        "max_write_buffer_number=4;" +
        "";
    public string? FlatStateTopNodesDbAdditionalRocksDbOptions { get; set; }

    // Most writes
    public string? FlatStorageNodesDbRocksDbOptions { get; set; } =
        FlatDbCommonTrieOptions +
        // Slight increase to account for high writes
        "max_bytes_for_level_base=350000000;" +
        "write_buffer_size=64000000;" +
        "max_write_buffer_number=8;" +
        "";
    public string? FlatStorageNodesDbAdditionalRocksDbOptions { get; set; }

    public string? FlatFallbackNodesDbRocksDbOptions { get; set; } =
        FlatDbCommonTrieOptions +
        // Fallback nodes is tiny. Like KB level small. This is generous.
        "max_bytes_for_level_base=4000000;" +
        "";
    public string? FlatFallbackNodesDbAdditionalRocksDbOptions { get; set; }

    // History columns (archival queries). As-of-block reads are iterator floor-seeks, which don't consult the point
    // bloom filter, so optimize_filters_for_hits drops the last-level bloom — its memory cost is linear in key count
    // and prohibitive on a full archive. A large write buffer cuts flushes during the from-genesis replay.
    // LZ4 over the Snappy default: benchmarked on history-shaped data at the same on-disk size but ~1.5x the seek
    // throughput and ~25% less compaction time (matching the flat Account/Storage columns' choice).
    const string FlatHistoryCommonOptions =
        "compression=kLZ4Compression;" +
        "optimize_filters_for_hits=true;" +
        "write_buffer_size=256000000;" +
        "max_write_buffer_number=4;" +
        "";

    public string FlatHistoryDbRocksDbOptions { get; set; } = FlatHistoryCommonOptions;
    public string? FlatHistoryDbAdditionalRocksDbOptions { get; set; }

    // The replay-sized write buffers matter only for the two bulky value columns.
    public string? FlatHistoryAvailableBlocksDbRocksDbOptions { get; set; } = "write_buffer_size=8000000;max_write_buffer_number=2;";
    public string? FlatHistoryStorageClearsDbRocksDbOptions { get; set; } = "write_buffer_size=8000000;max_write_buffer_number=2;";

    public string? PreimageDbRocksDbOptions { get; set; } = "";
    public string? PreimageDbAdditionalRocksDbOptions { get; set; }

    public string? PersistedSnapshotCatalogDbRocksDbOptions { get; set; } = "";
    public string? PersistedSnapshotCatalogDbAdditionalRocksDbOptions { get; set; }
}
