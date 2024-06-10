// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Config;

namespace Nethermind.Db.Rocks.Config;

[ConfigCategory(HiddenFromDocs = true)]
public interface IDbConfig : IConfig
{
    ulong SharedBlockCacheSize { get; set; }
    public bool SkipMemoryHintSetting { get; set; }

    ulong WriteBufferSize { get; set; }
    uint WriteBufferNumber { get; set; }
    ulong BlockCacheSize { get; set; }
    bool CacheIndexAndFilterBlocks { get; set; }
    int? MaxOpenFiles { get; set; }
    uint RecycleLogFileNum { get; set; }
    bool WriteAheadLogSync { get; set; }
    long? MaxBytesPerSec { get; set; }
    int? BlockSize { get; set; }
    ulong? ReadAheadSize { get; set; }
    bool? UseDirectReads { get; set; }
    bool? UseDirectIoForFlushAndCompactions { get; set; }
    bool? DisableCompression { get; set; }
    bool? UseLz4 { get; set; }
    ulong? CompactionReadAhead { get; set; }
    string? AdditionalRocksDbOptions { get; set; }
    ulong? MaxBytesForLevelBase { get; set; }
    ulong TargetFileSizeBase { get; set; }
    int TargetFileSizeMultiplier { get; set; }
    bool UseTwoLevelIndex { get; set; }
    bool UseHashIndex { get; set; }
    ulong? PrefixExtractorLength { get; set; }
    bool AllowMmapReads { get; set; }
    bool? VerifyChecksum { get; set; }
    double MaxBytesForLevelMultiplier { get; set; }
    ulong? MaxCompactionBytes { get; set; }
    int MinWriteBufferNumberToMerge { get; set; }
    ulong? RowCacheSize { get; set; }
    bool OptimizeFiltersForHits { get; set; }
    bool OnlyCompressLastLevel { get; set; }
    long? MaxWriteBufferSizeToMaintain { get; set; }
    bool UseHashSkipListMemtable { get; set; }
    int? BlockRestartInterval { get; set; }
    double MemtablePrefixBloomSizeRatio { get; set; }
    bool AdviseRandomOnOpen { get; set; }
    bool LevelCompactionDynamicLevelBytes { get; set; }
    int? BloomFilterBitsPerKey { get; set; }
    int? UseRibbonFilterStartingFromLevel { get; set; }
    ulong BytesPerSync { get; set; }
    double? DataBlockIndexUtilRatio { get; set; }
    bool EnableFileWarmer { get; set; }
    double CompressibilityHint { get; set; }

    ulong BlobTransactionsDbBlockCacheSize { get; set; }

    ulong ReceiptsDbWriteBufferSize { get; set; }
    uint ReceiptsDbWriteBufferNumber { get; set; }
    ulong ReceiptsDbBlockCacheSize { get; set; }
    bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; }
    int? ReceiptsDbMaxOpenFiles { get; set; }
    long? ReceiptsDbMaxBytesPerSec { get; set; }
    int? ReceiptsDbBlockSize { get; set; }
    bool? ReceiptsDbUseDirectReads { get; set; }
    bool? ReceiptsDbUseDirectIoForFlushAndCompactions { get; set; }
    ulong? ReceiptsDbCompactionReadAhead { get; set; }
    ulong ReceiptsDbTargetFileSizeBase { get; set; }
    double ReceiptsDbCompressibilityHint { get; set; }
    string? ReceiptsDbAdditionalRocksDbOptions { get; set; }

    ulong BlocksDbWriteBufferSize { get; set; }
    uint BlocksDbWriteBufferNumber { get; set; }
    ulong BlocksDbBlockCacheSize { get; set; }
    bool BlocksDbCacheIndexAndFilterBlocks { get; set; }
    int? BlocksDbMaxOpenFiles { get; set; }
    long? BlocksDbMaxBytesPerSec { get; set; }
    int? BlocksBlockSize { get; set; }
    bool? BlocksDbUseDirectReads { get; set; }
    bool? BlocksDbUseDirectIoForFlushAndCompactions { get; set; }
    ulong? BlocksDbCompactionReadAhead { get; set; }
    string? BlocksDbAdditionalRocksDbOptions { get; set; }

    ulong HeadersDbWriteBufferSize { get; set; }
    uint HeadersDbWriteBufferNumber { get; set; }
    ulong HeadersDbBlockCacheSize { get; set; }
    bool HeadersDbCacheIndexAndFilterBlocks { get; set; }
    int? HeadersDbMaxOpenFiles { get; set; }
    long? HeadersDbMaxBytesPerSec { get; set; }
    int? HeadersDbBlockSize { get; set; }
    bool? HeadersDbUseDirectReads { get; set; }
    bool? HeadersDbUseDirectIoForFlushAndCompactions { get; set; }
    ulong? HeadersDbCompactionReadAhead { get; set; }
    string? HeadersDbAdditionalRocksDbOptions { get; set; }
    ulong? HeadersDbMaxBytesForLevelBase { get; set; }

    ulong BlockNumbersDbWriteBufferSize { get; set; }
    uint BlockNumbersDbWriteBufferNumber { get; set; }
    ulong BlockNumbersDbBlockCacheSize { get; set; }
    bool BlockNumbersDbCacheIndexAndFilterBlocks { get; set; }
    int? BlockNumbersDbMaxOpenFiles { get; set; }
    long? BlockNumbersDbMaxBytesPerSec { get; set; }
    int? BlockNumbersDbBlockSize { get; set; }
    bool BlockNumbersDbUseHashIndex { get; set; }
    ulong? BlockNumbersDbRowCacheSize { get; set; }
    bool? BlockNumbersDbUseHashSkipListMemtable { get; set; }
    bool? BlockNumbersDbUseDirectReads { get; set; }
    bool? BlockNumbersDbUseDirectIoForFlushAndCompactions { get; set; }
    ulong? BlockNumbersDbCompactionReadAhead { get; set; }
    string? BlockNumbersDbAdditionalRocksDbOptions { get; set; }
    ulong? BlockNumbersDbMaxBytesForLevelBase { get; set; }

    ulong BlockInfosDbWriteBufferSize { get; set; }
    uint BlockInfosDbWriteBufferNumber { get; set; }
    ulong BlockInfosDbBlockCacheSize { get; set; }
    bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; }
    int? BlockInfosDbMaxOpenFiles { get; set; }
    long? BlockInfosDbMaxBytesPerSec { get; set; }
    int? BlockInfosDbBlockSize { get; set; }
    bool? BlockInfosDbUseDirectReads { get; set; }
    bool? BlockInfosDbUseDirectIoForFlushAndCompactions { get; set; }
    ulong? BlockInfosDbCompactionReadAhead { get; set; }
    string? BlockInfosDbAdditionalRocksDbOptions { get; set; }

    ulong PendingTxsDbWriteBufferSize { get; set; }
    uint PendingTxsDbWriteBufferNumber { get; set; }
    ulong PendingTxsDbBlockCacheSize { get; set; }
    bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; }
    int? PendingTxsDbMaxOpenFiles { get; set; }
    long? PendingTxsDbMaxBytesPerSec { get; set; }
    int? PendingTxsDbBlockSize { get; set; }
    bool? PendingTxsDbUseDirectReads { get; set; }
    bool? PendingTxsDbUseDirectIoForFlushAndCompactions { get; set; }
    ulong? PendingTxsDbCompactionReadAhead { get; set; }
    string? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    ulong CodeDbWriteBufferSize { get; set; }
    uint CodeDbWriteBufferNumber { get; set; }
    ulong CodeDbBlockCacheSize { get; set; }
    bool CodeDbCacheIndexAndFilterBlocks { get; set; }
    int? CodeDbMaxOpenFiles { get; set; }
    long? CodeDbMaxBytesPerSec { get; set; }
    int? CodeDbBlockSize { get; set; }
    bool CodeDbUseHashIndex { get; set; }
    ulong? CodeDbRowCacheSize { get; set; }
    bool? CodeDbUseHashSkipListMemtable { get; set; }
    bool? CodeUseDirectReads { get; set; }
    bool? CodeUseDirectIoForFlushAndCompactions { get; set; }
    ulong? CodeCompactionReadAhead { get; set; }
    string? CodeDbAdditionalRocksDbOptions { get; set; }

    ulong BloomDbWriteBufferSize { get; set; }
    uint BloomDbWriteBufferNumber { get; set; }
    ulong BloomDbBlockCacheSize { get; set; }
    bool BloomDbCacheIndexAndFilterBlocks { get; set; }
    int? BloomDbMaxOpenFiles { get; set; }
    long? BloomDbMaxBytesPerSec { get; set; }
    string? BloomDbAdditionalRocksDbOptions { get; set; }

    ulong MetadataDbWriteBufferSize { get; set; }
    uint MetadataDbWriteBufferNumber { get; set; }
    ulong MetadataDbBlockCacheSize { get; set; }
    bool MetadataDbCacheIndexAndFilterBlocks { get; set; }
    int? MetadataDbMaxOpenFiles { get; set; }
    long? MetadataDbMaxBytesPerSec { get; set; }
    int? MetadataDbBlockSize { get; set; }
    bool? MetadataUseDirectReads { get; set; }
    bool? MetadataUseDirectIoForFlushAndCompactions { get; set; }
    ulong? MetadataCompactionReadAhead { get; set; }
    string? MetadataDbAdditionalRocksDbOptions { get; set; }

    ulong StateDbWriteBufferSize { get; set; }
    uint StateDbWriteBufferNumber { get; set; }
    ulong StateDbBlockCacheSize { get; set; }
    bool StateDbCacheIndexAndFilterBlocks { get; set; }
    int? StateDbMaxOpenFiles { get; set; }
    long? StateDbMaxBytesPerSec { get; set; }
    int? StateDbBlockSize { get; set; }
    bool? StateDbUseDirectReads { get; set; }
    bool? StateDbUseDirectIoForFlushAndCompactions { get; set; }
    ulong? StateDbCompactionReadAhead { get; set; }
    bool? StateDbDisableCompression { get; set; }
    bool? StateDbUseLz4 { get; set; }
    int StateDbTargetFileSizeMultiplier { get; set; }
    bool StateDbUseTwoLevelIndex { get; set; }
    bool StateDbUseHashIndex { get; set; }
    ulong? StateDbPrefixExtractorLength { get; set; }
    bool StateDbAllowMmapReads { get; set; }
    bool? StateDbVerifyChecksum { get; set; }
    double StateDbMaxBytesForLevelMultiplier { get; set; }
    ulong? StateDbMaxBytesForLevelBase { get; set; }
    ulong? StateDbMaxCompactionBytes { get; set; }
    int StateDbMinWriteBufferNumberToMerge { get; set; }
    ulong? StateDbRowCacheSize { get; set; }
    bool StateDbOptimizeFiltersForHits { get; set; }
    bool StateDbOnlyCompressLastLevel { get; set; }
    long? StateDbMaxWriteBufferSizeToMaintain { get; set; }
    bool StateDbUseHashSkipListMemtable { get; set; }
    int? StateDbBlockRestartInterval { get; set; }
    double StateDbMemtablePrefixBloomSizeRatio { get; set; }
    bool StateDbAdviseRandomOnOpen { get; set; }
    int? StateDbBloomFilterBitsPerKey { get; set; }
    int? StateDbUseRibbonFilterStartingFromLevel { get; set; }
    double? StateDbDataBlockIndexUtilRatio { get; set; }
    bool StateDbEnableFileWarmer { get; set; }
    double StateDbCompressibilityHint { get; set; }
    string? StateDbAdditionalRocksDbOptions { get; set; }

    /// <summary>
    /// Enables DB Statistics - https://github.com/facebook/rocksdb/wiki/Statistics
    /// It can has a RocksDB performance hit between 5 and 10%.
    /// </summary>
    bool EnableDbStatistics { get; set; }
    bool EnableMetricsUpdater { get; set; }
    /// <summary>
    /// If not zero, dump rocksdb.stats to LOG every stats_dump_period_sec
    /// Default: 600 (10 min)
    /// </summary>
    uint StatsDumpPeriodSec { get; set; }
}
