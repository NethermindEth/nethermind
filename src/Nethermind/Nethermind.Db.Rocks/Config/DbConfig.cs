// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Db.Rocks.Config;

public class DbConfig : IDbConfig
{
    public static DbConfig Default = new DbConfig();

    public ulong SharedBlockCacheSize { get; set; } = (ulong)256.MiB();
    public bool SkipMemoryHintSetting { get; set; } = false;

    public ulong WriteBufferSize { get; set; } = (ulong)16.MiB();
    public uint WriteBufferNumber { get; set; } = 2;
    public ulong BlockCacheSize { get; set; } = 0;
    public int? MaxOpenFiles { get; set; }
    public long? MaxBytesPerSec { get; set; }
    public ulong? ReadAheadSize { get; set; } = (ulong)256.KiB();

    public string? AdditionalRocksDbOptions { get; set; } =
          "compression=kSnappyCompression;"
        + "optimize_filters_for_hits=true;"
        + "memtable_whole_key_filtering=true;"
        + "memtable_prefix_bloom_size_ratio=0.02;"
        + "advise_random_on_open=true;"

        // Note, this is before compression. On disk size may be lower. The on disk size is the minimum amount of read
        // each io will do. On most SSD, the minimum read size is 4096 byte. So don't set it to lower than that, unless
        // you have an optane drive or some kind of RAM disk. Lower block size also means bigger index size.
        + "block_based_table_factory.block_size=16000;"

        // No significant downside. Just set it.
        + "block_based_table_factory.pin_l0_filter_and_index_blocks_in_cache=true;"

        // Make the index in cache have higher priority, so it is kept more in cache.
        + "block_based_table_factory.cache_index_and_filter_blocks_with_high_priority=true;"

        + "block_based_table_factory.format_version=5;"

        // Two level index split the index into two level. First index point to second level index, which actually
        // point to the block, which get bsearched to the value. This means potentially two iop instead of one per
        // read, and probably more processing overhead. But it significantly reduces memory usage and make block
        // processing time more consistent. So its enabled by default. That said, if you got the RAM, maybe disable
        // this.
        // See https://rocksdb.org/blog/2017/05/12/partitioned-index-filter.html
        + "block_based_table_factory.index_type=kTwoLevelIndexSearch;"
        + "block_based_table_factory.partition_filters=true;"
        + "block_based_table_factory.metadata_block_size=4096;"


        ;

    public ulong? MaxBytesForLevelBase { get; set; } = (ulong)256.MiB();
    public ulong TargetFileSizeBase { get; set; } = (ulong)64.MiB();
    public int TargetFileSizeMultiplier { get; set; } = 1;
    public ulong? PrefixExtractorLength { get; set; } = null;
    public bool? VerifyChecksum { get; set; } = true;
    public double MaxBytesForLevelMultiplier { get; set; } = 10;
    public int MinWriteBufferNumberToMerge { get; set; } = 1;
    public ulong? RowCacheSize { get; set; } = null;
    public long? MaxWriteBufferSizeToMaintain { get; set; } = null;
    public bool UseHashSkipListMemtable { get; set; } = false;
    public int? BlockRestartInterval { get; set; } = 16;
    public int? BloomFilterBitsPerKey { get; set; } = 10;
    public int? UseRibbonFilterStartingFromLevel { get; set; }
    public double? DataBlockIndexUtilRatio { get; set; }
    public bool EnableFileWarmer { get; set; } = false;
    public double CompressibilityHint { get; set; } = 1.0;

    public ulong BlobTransactionsDbBlockCacheSize { get; set; } = (ulong)32.MiB();

    public ulong ReceiptsDbWriteBufferSize { get; set; } = (ulong)2.MiB();
    public uint ReceiptsDbWriteBufferNumber { get; set; } = 2;
    public ulong ReceiptsDbBlockCacheSize { get; set; } = (ulong)8.MiB();
    public int? ReceiptsDbMaxOpenFiles { get; set; }
    public long? ReceiptsDbMaxBytesPerSec { get; set; }
    public ulong ReceiptsDbTargetFileSizeBase { get; set; } = (ulong)64.MiB();
    public double ReceiptsDbCompressibilityHint { get; set; } = 0.35;
    public string? ReceiptsDbAdditionalRocksDbOptions { get; set; } = "compaction_pri=kOldestLargestSeqFirst;optimize_filters_for_hits=false;";

    public ulong BlocksDbWriteBufferSize { get; set; } = (ulong)64.MiB();
    public uint BlocksDbWriteBufferNumber { get; set; } = 2;
    public ulong BlocksDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public int? BlocksDbMaxOpenFiles { get; set; }
    public long? BlocksDbMaxBytesPerSec { get; set; }
    public string? BlocksDbAdditionalRocksDbOptions { get; set; } = "compaction_pri=kOldestLargestSeqFirst;optimize_filters_for_hits=false;";

    public ulong HeadersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint HeadersDbWriteBufferNumber { get; set; } = 2;
    public ulong HeadersDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public int? HeadersDbMaxOpenFiles { get; set; }
    public long? HeadersDbMaxBytesPerSec { get; set; }
    public string? HeadersDbAdditionalRocksDbOptions { get; set; } = "compaction_pri=kOldestLargestSeqFirst;block_based_table_factory.block_size=32000;";
    public ulong? HeadersDbMaxBytesForLevelBase { get; set; } = (ulong)128.MiB();

    public ulong BlockNumbersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlockNumbersDbWriteBufferNumber { get; set; } = 2;
    public ulong BlockNumbersDbBlockCacheSize { get; set; }
    public int? BlockNumbersDbMaxOpenFiles { get; set; }
    public long? BlockNumbersDbMaxBytesPerSec { get; set; }
    public ulong? BlockNumbersDbRowCacheSize { get; set; } = (ulong)16.MiB();
    public bool? BlockNumbersDbUseHashSkipListMemtable { get; set; } = true;
    public string? BlockNumbersDbAdditionalRocksDbOptions { get; set; } = "block_based_table_factory.block_size=4096;";
    public ulong? BlockNumbersDbMaxBytesForLevelBase { get; set; } = (ulong)16.MiB();

    public ulong BlockInfosDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint BlockInfosDbWriteBufferNumber { get; set; } = 2;
    public ulong BlockInfosDbBlockCacheSize { get; set; } = (ulong)16.MiB();
    public int? BlockInfosDbMaxOpenFiles { get; set; }
    public long? BlockInfosDbMaxBytesPerSec { get; set; }
    public string? BlockInfosDbAdditionalRocksDbOptions { get; set; } = "compaction_pri=kOldestLargestSeqFirst";

    public ulong PendingTxsDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint PendingTxsDbWriteBufferNumber { get; set; } = 4;
    public ulong PendingTxsDbBlockCacheSize { get; set; } = 0;
    public int? PendingTxsDbMaxOpenFiles { get; set; }
    public long? PendingTxsDbMaxBytesPerSec { get; set; }
    public string? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    public ulong CodeDbWriteBufferSize { get; set; } = (ulong)1.MiB();
    public uint CodeDbWriteBufferNumber { get; set; } = 2;
    public ulong CodeDbBlockCacheSize { get; set; } = 0;
    public int? CodeDbMaxOpenFiles { get; set; }
    public long? CodeDbMaxBytesPerSec { get; set; }
    public ulong? CodeDbRowCacheSize { get; set; } = (ulong)16.MiB();
    public bool? CodeDbUseHashSkipListMemtable { get; set; } = true;
    public string? CodeDbAdditionalRocksDbOptions { get; set; } = "block_based_table_factory.block_size=4096;";

    public ulong BloomDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint BloomDbWriteBufferNumber { get; set; } = 4;
    public ulong BloomDbBlockCacheSize { get; set; } = 0;
    public int? BloomDbMaxOpenFiles { get; set; }
    public long? BloomDbMaxBytesPerSec { get; set; }
    public string? BloomDbAdditionalRocksDbOptions { get; set; }

    public ulong MetadataDbWriteBufferSize { get; set; } = (ulong)1.KiB();
    public uint MetadataDbWriteBufferNumber { get; set; } = 4;
    public ulong MetadataDbBlockCacheSize { get; set; } = 0;
    public int? MetadataDbMaxOpenFiles { get; set; }
    public long? MetadataDbMaxBytesPerSec { get; set; }
    public string? MetadataDbAdditionalRocksDbOptions { get; set; }

    public ulong StateDbWriteBufferSize { get; set; } = (ulong)64.MB();
    public uint StateDbWriteBufferNumber { get; set; } = 4;
    public ulong StateDbBlockCacheSize { get; set; }
    public int? StateDbMaxOpenFiles { get; set; }
    public long? StateDbMaxBytesPerSec { get; set; }
    public int StateDbTargetFileSizeMultiplier { get; set; } = 2;
    public ulong? StateDbPrefixExtractorLength { get; set; } = null;
    public bool? StateDbVerifyChecksum { get; set; }
    public double StateDbMaxBytesForLevelMultiplier { get; set; } = 30;
    public ulong? StateDbMaxBytesForLevelBase { get; set; } = (ulong)350.MiB();
    public int StateDbMinWriteBufferNumberToMerge { get; set; } = 2;
    public ulong? StateDbRowCacheSize { get; set; }
    public long? StateDbMaxWriteBufferSizeToMaintain { get; set; }
    public bool StateDbUseHashSkipListMemtable { get; set; } = false;
    public int? StateDbBlockRestartInterval { get; set; } = 4;
    public int? StateDbBloomFilterBitsPerKey { get; set; } = 15;
    public int? StateDbUseRibbonFilterStartingFromLevel { get; set; } = 2;
    public double? StateDbDataBlockIndexUtilRatio { get; set; } = 0.5;
    public bool StateDbEnableFileWarmer { get; set; } = false;
    public double StateDbCompressibilityHint { get; set; } = 0.45;

    public string? StateDbAdditionalRocksDbOptions { get; set; } =
        "compression=kLZ4Compression;block_based_table_factory.block_size=32000;";

    public bool WriteAheadLogSync { get; set; } = false;

    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;
}
