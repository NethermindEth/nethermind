// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Db.Rocks.Config;

public class DbConfig : IDbConfig
{
    public static DbConfig Default = new DbConfig();

    public ulong SharedBlockCacheSize { get; set; } = (ulong)256.MiB();
    public bool SkipMemoryHintSetting { get; set; } = false;

    public bool WriteAheadLogSync { get; set; } = false;
    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;

    public int? MaxOpenFiles { get; set; }
    public ulong? ReadAheadSize { get; set; } = (ulong)256.KiB();

    public string RocksDbOptions { get; set; } =

        // This section affect the write buffer, or memtable. Note, the size of write buffer affect the size of l0
        // file which affect compactions. The options here does not effect how the sst files are read... probably.
        // But read does go through the write buffer first, before going through the rowcache (or is it before memtable?)
        // block cache and then finally the LSM/SST files.
        "min_write_buffer_number_to_merge=1;" +
        "write_buffer_size=16000000;" +
        "max_write_buffer_number=2;" +
        "memtable_whole_key_filtering=true;" +
        "memtable_prefix_bloom_size_ratio=0.02;" +

        // Rocksdb turn this on by default a few release ago. But we dont want it yet, not sure the impact on read is
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
        // point to the block, which get bsearched to the value. This means potentially two iop instead of one per
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
    public bool FlushOnExit { get; set; } = true;

    public string BadBlocksDbRocksDbOptions { get; set; } = "";
    public string? BadBlocksDbAdditionalRocksDbOptions { get; set; }


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
        "compaction_pri=kOldestLargestSeqFirst;";
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

    public ulong? BlockNumbersDbRowCacheSize { get; set; } = (ulong)16.MiB();
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

    public ulong? CodeDbRowCacheSize { get; set; } = (ulong)16.MiB();
    public string CodeDbRocksDbOptions { get; set; } =
        "write_buffer_size=4000000;" +
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

    public string BloomDbRocksDbOptions { get; set; } =
        "max_bytes_for_level_base=16000000;";
    public string? BloomDbAdditionalRocksDbOptions { get; set; }

    public string MetadataDbRocksDbOptions { get; set; } =
        "write_buffer_size=1000000;" +
        "max_bytes_for_level_base=16000000;";
    public string? MetadataDbAdditionalRocksDbOptions { get; set; }

    public ulong StateDbWriteBufferSize { get; set; } = (ulong)64.MB();
    public ulong StateDbWriteBufferNumber { get; set; } = 4;
    public bool? StateDbVerifyChecksum { get; set; }
    public ulong? StateDbRowCacheSize { get; set; }
    public bool StateDbEnableFileWarmer { get; set; } = false;
    public double StateDbCompressibilityHint { get; set; } = 0.45;
    public string StateDbRocksDbOptions { get; set; } =
        // LZ4 seems to be slightly faster here
        "compression=kLZ4Compression;" +

        // MaxBytesForLevelMultiplier is 10 by default. Lowering this will deepens the LSM, which may reduce write
        // amplification (unless the LSM is too deep), at the expense of read performance. But then, you have bloom
        // filter anyway, and recently written keys are likely to be read and they tend to be at the top of the LSM
        // tree which means they are more cacheable, so at that point you are trading CPU for cacheability.
        // These two config make the LSM level to be no more than 3 until the database grow to about 250GB.
        "max_bytes_for_level_multiplier=30;" +
        "max_bytes_for_level_base=350000000;" +

        // Multiply the target size of SST file by this much every level down, reduce number of file.
        // Does not have much downside on hash based DB, but might disable some move optimization on db with
        // blocknumber key, or halfpath/flatdb layout.
        "target_file_size_multiplier=2;" +

        // This is basically useless on write only database. However, for halfpath with live pruning, flatdb, or
        // (maybe?) full sync where keys are deleted, replaced, or re-inserted, two memtable can merge together
        // resulting in a reduced total memtable size to be written. This does seems to reduce sync throughput though.
        "min_write_buffer_number_to_merge=2;" +

        // Default value is 16.
        // So each block consist of several "restart" and each "restart" is BlockRestartInterval number of key.
        // They key within the same restart is delta-encoded with the key before it. This mean a read will have to go
        // through potentially "BlockRestartInterval" number of key, probably. That is my understanding.
        // Reducing this is likely going to improve CPU usage at the cost of increased uncompressed size, which effect
        // cache utilization.
        "block_based_table_factory.block_restart_interval=4;" +

        // This adds a hashtable-like index per block (the 32kb block)
        // This reduce CPU and therefore latency under high block cache hit scenario.
        // It seems to increase disk space use by about 1 GB.
        "block_based_table_factory.data_block_index_type=kDataBlockBinaryAndHash;" +
        "block_based_table_factory.data_block_hash_table_util_ratio=0.5;" +

        "block_based_table_factory.block_size=32000;" +

        "block_based_table_factory.filter_policy=bloomfilter:15;" +

        // Note: This causes write batch to not be atomic. A concurrent read may read item on start of batch, but not end of batch.
        // With state, this is fine as writes are done in parallel batch and therefore, not atomic, and the read goes
        // through triestore first anyway.
        "unordered_write=true;" +

        // Default is 1 MB.
        "max_write_batch_group_size_bytes=4000000;" +

        "";
    public string? StateDbAdditionalRocksDbOptions { get; set; }

    public string L1OriginDbRocksDbOptions { get; set; } = "";

    public string? L1OriginDbAdditionalRocksDbOptions { get; set; }

    // TODO: cleanup & optimize settings
    public string? LogIndexStorageDbRocksDbOptions { get; set; } = "";
    public string? LogIndexStorageDbAdditionalRocksDbOptions { get; set; } = "";
    public string? LogIndexStorageDefaultDbRocksDbOptions { get; set; } = "";
    public string? LogIndexStorageDefaultDbAdditionalRocksDbOptions { get; set; } = "";
    public string? LogIndexStorageAddressesDbRocksDbOptions { get; set; } = "";
    public string? LogIndexStorageAddressesDbAdditionalRocksDbOptions { get; set; } = "";
    public string? LogIndexStorageTopicsDbRocksDbOptions { get; set; } = "";
    public string? LogIndexStorageTopicsDbAdditionalRocksDbOptions { get; set; } = "";
}
