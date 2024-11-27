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



    public ulong WriteBufferSize { get; set; } = (ulong)16.MiB();
    public uint WriteBufferNumber { get; set; } = 2;
    public int? MaxOpenFiles { get; set; }
    public ulong? ReadAheadSize { get; set; } = (ulong)256.KiB();

    public string RocksDbOptions { get; set; } =

        // Rocksdb turn this on by default a few release ago. But we dont want it yet, not sure the impact on read is
        // significant or not.
        "level_compaction_dynamic_level_bytes=false;" +

        "compression=kSnappyCompression;" +
        "optimize_filters_for_hits=true;" +
        "memtable_whole_key_filtering=true;" +
        "memtable_prefix_bloom_size_ratio=0.02;" +
        "advise_random_on_open=true;" +
        "min_write_buffer_number_to_merge=1;" +

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

        "block_based_table_factory.filter_policy=bloom_filter:10;" +
        "";

    public string? AdditionalRocksDbOptions { get; set; }

    public bool? VerifyChecksum { get; set; } = true;
    public ulong? RowCacheSize { get; set; } = null;
    public bool EnableFileWarmer { get; set; } = false;
    public double CompressibilityHint { get; set; } = 1.0;


    public string BlobTransactionsDbRocksDbOptions { get; set; } =
        "block_based_table_factory.block_cache=32000000;";
    public string? BlobTransactionsDbAdditionalRocksDbOptions { get; set; }


    public ulong ReceiptsDbWriteBufferSize { get; set; } = (ulong)2.MiB();
    public double ReceiptsDbCompressibilityHint { get; set; } = 0.35;
    public string ReceiptsDbRocksDbOptions { get; set; } =
        "block_based_table_factory.block_cache=8000000;" +
        "compaction_pri=kOldestLargestSeqFirst;" +
        "optimize_filters_for_hits=false;";
    public string? ReceiptsDbAdditionalRocksDbOptions { get; set; } = "";

    public ulong BlocksDbWriteBufferSize { get; set; } = (ulong)64.MiB();
    public string BlocksDbRocksDbOptions { get; set; } =
        "block_based_table_factory.block_cache=32000000;" +
        "compaction_pri=kOldestLargestSeqFirst;" +
        "optimize_filters_for_hits=false;";
    public string? BlocksDbAdditionalRocksDbOptions { get; set; } = "";

    public ulong HeadersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public string HeadersDbRocksDbOptions { get; set; } =
        "block_based_table_factory.block_cache=32000000;" +
        "compaction_pri=kOldestLargestSeqFirst;" +
        "block_based_table_factory.block_size=32000;" +
        "max_bytes_for_level_base=128000000;" +
        "";
    public string? HeadersDbAdditionalRocksDbOptions { get; set; } = "";

    public ulong BlockNumbersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public ulong? BlockNumbersDbRowCacheSize { get; set; } = (ulong)16.MiB();
    public string BlockNumbersDbRocksDbOptions { get; set; } =
        "block_based_table_factory.block_cache=16000000;" +
        "block_based_table_factory.block_size=4096;" +
        "max_bytes_for_level_base=16000000;" +
        "memtable=prefix_hash:1000000;" +
        "allow_concurrent_memtable_write=false;" +
        "";
    public string? BlockNumbersDbAdditionalRocksDbOptions { get; set; } = "";

    public ulong BlockInfosDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public string BlockInfosDbRocksDbOptions { get; set; } =
        "block_based_table_factory.block_cache=16000000;" +
        "compaction_pri=kOldestLargestSeqFirst;";
    public string? BlockInfosDbAdditionalRocksDbOptions { get; set; } = "";

    public ulong PendingTxsDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public string PendingTxsDbRocksDbOptions { get; set; } = "";
    public string? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    public ulong CodeDbWriteBufferSize { get; set; } = (ulong)1.MiB();
    public ulong? CodeDbRowCacheSize { get; set; } = (ulong)16.MiB();
    public string CodeDbRocksDbOptions { get; set; } =
        "block_based_table_factory.block_cache=16000000;" +
        "prefix_extractor=capped:16;" +
        "block_based_table_factory.index_type=kHashSearch;" +
        "block_based_table_factory.block_size=4096;" +
        "memtable=prefix_hash:1000000;" +
        "allow_concurrent_memtable_write=false;";
    public string? CodeDbAdditionalRocksDbOptions { get; set; }

    public ulong BloomDbWriteBufferSize { get; set; } = (ulong)1.MiB();
    public string BloomDbRocksDbOptions { get; set; } =
        "max_bytes_for_level_base=16000000;";
    public string? BloomDbAdditionalRocksDbOptions { get; set; }

    public ulong MetadataDbWriteBufferSize { get; set; } = (ulong)1.MiB();
    public string MetadataDbRocksDbOptions { get; set; } =
        "max_bytes_for_level_base=16000000;";
    public string? MetadataDbAdditionalRocksDbOptions { get; set; }

    public ulong StateDbWriteBufferSize { get; set; } = (ulong)64.MB();
    public uint StateDbWriteBufferNumber { get; set; } = 4;
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

        // Causes file size to double per level. Lower total number of file.
        "target_file_size_multiplier=2;" +

        // This is basically useless on write only database. However, for halfpath with live pruning, flatdb, or
        // (maybe?) full sync where keys are deleted, replaced, or re-inserted, two memtable can merge together
        // resulting in a reduced total memtable size to be written. This does seems to reduce sync throughput though.
        "min_write_buffer_number_to_merge=2;" +

        // Default value is 16.
        // So each block consist of several "restart" and each "restart" is BlockRestartInterval number of key.
        // They key within the same restart is delta-encoded with the key before it. This mean a read will have to go
        // through a minimum of "BlockRestartInterval" number of key, probably. That is my understanding.
        // Reducing this is likely going to improve CPU usage at the cost of increased uncompressed size, which effect
        // cache utilization.
        "block_based_table_factory.block_restart_interval=4;" +

        // This adds a hashtable-like index per block (the 32kb block)
        // In, this reduce CPU and therefore latency under high block cache hit scenario.
        // It seems to increase disk space use by about 1 GB.
        "block_based_table_factory.data_block_index_type=kDataBlockBinaryAndHash;" +
        "block_based_table_factory.data_block_hash_table_util_ratio=0.5;" +

        "block_based_table_factory.block_size=32000;" +

        "block_based_table_factory.filter_policy=bloom_filter:15;" +
        "";
    public string? StateDbAdditionalRocksDbOptions { get; set; }
}
