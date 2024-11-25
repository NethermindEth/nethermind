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
    public int? BlockSize { get; set; } = 16 * 1024;
    public ulong? ReadAheadSize { get; set; } = (ulong)256.KiB();
    public bool? UseDirectReads { get; set; } = false;
    public bool? UseDirectIoForFlushAndCompactions { get; set; } = false;
    public bool? DisableCompression { get; set; } = false;
    public string? AdditionalRocksDbOptions { get; set; } = "compression=kSnappyCompression;optimize_filters_for_hits=true;memtable_whole_key_filtering=true;memtable_prefix_bloom_size_ratio=0.02;";
    public ulong? MaxBytesForLevelBase { get; set; } = (ulong)256.MiB();
    public ulong TargetFileSizeBase { get; set; } = (ulong)64.MiB();
    public int TargetFileSizeMultiplier { get; set; } = 1;
    public bool UseTwoLevelIndex { get; set; } = true;
    public bool UseHashIndex { get; set; } = false;
    public ulong? PrefixExtractorLength { get; set; } = null;
    public bool? VerifyChecksum { get; set; } = true;
    public double MaxBytesForLevelMultiplier { get; set; } = 10;
    public int MinWriteBufferNumberToMerge { get; set; } = 1;
    public ulong? RowCacheSize { get; set; } = null;
    public long? MaxWriteBufferSizeToMaintain { get; set; } = null;
    public bool UseHashSkipListMemtable { get; set; } = false;
    public int? BlockRestartInterval { get; set; } = 16;
    public bool AdviseRandomOnOpen { get; set; } = true;
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
    public int? ReceiptsDbBlockSize { get; set; }
    public bool? ReceiptsDbUseDirectReads { get; set; }
    public bool? ReceiptsDbUseDirectIoForFlushAndCompactions { get; set; }
    public ulong ReceiptsDbTargetFileSizeBase { get; set; } = (ulong)64.MiB();
    public double ReceiptsDbCompressibilityHint { get; set; } = 0.35;
    public string? ReceiptsDbAdditionalRocksDbOptions { get; set; } = "compaction_pri=kOldestLargestSeqFirst;optimize_filters_for_hits=false;";

    public ulong BlocksDbWriteBufferSize { get; set; } = (ulong)64.MiB();
    public uint BlocksDbWriteBufferNumber { get; set; } = 2;
    public ulong BlocksDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public int? BlocksDbMaxOpenFiles { get; set; }
    public long? BlocksDbMaxBytesPerSec { get; set; }
    public int? BlocksBlockSize { get; set; }
    public bool? BlocksDbUseDirectReads { get; set; }
    public bool? BlocksDbUseDirectIoForFlushAndCompactions { get; set; }
    public string? BlocksDbAdditionalRocksDbOptions { get; set; } = "compaction_pri=kOldestLargestSeqFirst;optimize_filters_for_hits=false;";

    public ulong HeadersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint HeadersDbWriteBufferNumber { get; set; } = 2;
    public ulong HeadersDbBlockCacheSize { get; set; } = (ulong)32.MiB();
    public int? HeadersDbMaxOpenFiles { get; set; }
    public long? HeadersDbMaxBytesPerSec { get; set; }
    public int? HeadersDbBlockSize { get; set; } = 32 * 1024;
    public bool? HeadersDbUseDirectReads { get; set; }
    public bool? HeadersDbUseDirectIoForFlushAndCompactions { get; set; }
    public string? HeadersDbAdditionalRocksDbOptions { get; set; } = "compaction_pri=kOldestLargestSeqFirst";
    public ulong? HeadersDbMaxBytesForLevelBase { get; set; } = (ulong)128.MiB();

    public ulong BlockNumbersDbWriteBufferSize { get; set; } = (ulong)8.MiB();
    public uint BlockNumbersDbWriteBufferNumber { get; set; } = 2;
    public ulong BlockNumbersDbBlockCacheSize { get; set; }
    public int? BlockNumbersDbMaxOpenFiles { get; set; }
    public long? BlockNumbersDbMaxBytesPerSec { get; set; }
    public int? BlockNumbersDbBlockSize { get; set; } = 4 * 1024;
    public bool BlockNumbersDbUseHashIndex { get; set; } = true;
    public ulong? BlockNumbersDbRowCacheSize { get; set; } = (ulong)16.MiB();
    public bool? BlockNumbersDbUseHashSkipListMemtable { get; set; } = true;
    public bool? BlockNumbersDbUseDirectReads { get; set; }
    public bool? BlockNumbersDbUseDirectIoForFlushAndCompactions { get; set; }
    public string? BlockNumbersDbAdditionalRocksDbOptions { get; set; }
    public ulong? BlockNumbersDbMaxBytesForLevelBase { get; set; } = (ulong)16.MiB();

    public ulong BlockInfosDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint BlockInfosDbWriteBufferNumber { get; set; } = 2;
    public ulong BlockInfosDbBlockCacheSize { get; set; } = (ulong)16.MiB();
    public int? BlockInfosDbMaxOpenFiles { get; set; }
    public long? BlockInfosDbMaxBytesPerSec { get; set; }
    public int? BlockInfosDbBlockSize { get; set; }
    public bool? BlockInfosDbUseDirectReads { get; set; }
    public bool? BlockInfosDbUseDirectIoForFlushAndCompactions { get; set; }
    public string? BlockInfosDbAdditionalRocksDbOptions { get; set; } = "compaction_pri=kOldestLargestSeqFirst";

    public ulong PendingTxsDbWriteBufferSize { get; set; } = (ulong)4.MiB();
    public uint PendingTxsDbWriteBufferNumber { get; set; } = 4;
    public ulong PendingTxsDbBlockCacheSize { get; set; } = 0;
    public int? PendingTxsDbMaxOpenFiles { get; set; }
    public long? PendingTxsDbMaxBytesPerSec { get; set; }
    public int? PendingTxsDbBlockSize { get; set; }
    public bool? PendingTxsDbUseDirectReads { get; set; }
    public bool? PendingTxsDbUseDirectIoForFlushAndCompactions { get; set; }
    public string? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    public ulong CodeDbWriteBufferSize { get; set; } = (ulong)1.MiB();
    public uint CodeDbWriteBufferNumber { get; set; } = 2;
    public ulong CodeDbBlockCacheSize { get; set; } = 0;
    public int? CodeDbMaxOpenFiles { get; set; }
    public long? CodeDbMaxBytesPerSec { get; set; }
    public int? CodeDbBlockSize { get; set; } = 4 * 1024;
    public bool CodeDbUseHashIndex { get; set; } = true;
    public ulong? CodeDbRowCacheSize { get; set; } = (ulong)16.MiB();
    public bool? CodeDbUseHashSkipListMemtable { get; set; } = true;
    public bool? CodeUseDirectReads { get; set; }
    public bool? CodeUseDirectIoForFlushAndCompactions { get; set; }
    public string? CodeDbAdditionalRocksDbOptions { get; set; }

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
    public int? MetadataDbBlockSize { get; set; }
    public bool? MetadataUseDirectReads { get; set; }
    public bool? MetadataUseDirectIoForFlushAndCompactions { get; set; }
    public string? MetadataDbAdditionalRocksDbOptions { get; set; }

    public ulong StateDbWriteBufferSize { get; set; } = (ulong)64.MB();
    public uint StateDbWriteBufferNumber { get; set; } = 4;
    public ulong StateDbBlockCacheSize { get; set; }
    public int? StateDbMaxOpenFiles { get; set; }
    public long? StateDbMaxBytesPerSec { get; set; }
    public int? StateDbBlockSize { get; set; } = 32 * 1024;
    public bool? StateDbUseDirectReads { get; set; }
    public bool? StateDbUseDirectIoForFlushAndCompactions { get; set; }
    public bool? StateDbDisableCompression { get; set; }
    public int StateDbTargetFileSizeMultiplier { get; set; } = 2;
    public bool StateDbUseTwoLevelIndex { get; set; } = true;
    public bool StateDbUseHashIndex { get; set; } = false;
    public ulong? StateDbPrefixExtractorLength { get; set; } = null;
    public bool? StateDbVerifyChecksum { get; set; }
    public double StateDbMaxBytesForLevelMultiplier { get; set; } = 30;
    public ulong? StateDbMaxBytesForLevelBase { get; set; } = (ulong)350.MiB();
    public int StateDbMinWriteBufferNumberToMerge { get; set; } = 2;
    public ulong? StateDbRowCacheSize { get; set; }
    public long? StateDbMaxWriteBufferSizeToMaintain { get; set; }
    public bool StateDbUseHashSkipListMemtable { get; set; } = false;
    public int? StateDbBlockRestartInterval { get; set; } = 4;
    public bool StateDbAdviseRandomOnOpen { get; set; }
    public int? StateDbBloomFilterBitsPerKey { get; set; } = 15;
    public int? StateDbUseRibbonFilterStartingFromLevel { get; set; } = 2;
    public double? StateDbDataBlockIndexUtilRatio { get; set; } = 0.5;
    public bool StateDbEnableFileWarmer { get; set; } = false;
    public double StateDbCompressibilityHint { get; set; } = 0.45;
    public string? StateDbAdditionalRocksDbOptions { get; set; } = "compression=kLZ4Compression;";

    public bool WriteAheadLogSync { get; set; } = false;

    public bool EnableDbStatistics { get; set; } = false;
    public bool EnableMetricsUpdater { get; set; } = false;
    public uint StatsDumpPeriodSec { get; set; } = 600;
}
