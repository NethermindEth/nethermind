// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    int? MaxOpenFiles { get; set; }
    bool WriteAheadLogSync { get; set; }
    long? MaxBytesPerSec { get; set; }
    int? BlockSize { get; set; }
    ulong? ReadAheadSize { get; set; }
    string? AdditionalRocksDbOptions { get; set; }
    ulong? MaxBytesForLevelBase { get; set; }
    ulong TargetFileSizeBase { get; set; }
    int TargetFileSizeMultiplier { get; set; }
    ulong? PrefixExtractorLength { get; set; }
    bool? VerifyChecksum { get; set; }
    double MaxBytesForLevelMultiplier { get; set; }
    int MinWriteBufferNumberToMerge { get; set; }
    ulong? RowCacheSize { get; set; }
    bool UseHashSkipListMemtable { get; set; }
    int? BlockRestartInterval { get; set; }
    int? BloomFilterBitsPerKey { get; set; }
    int? UseRibbonFilterStartingFromLevel { get; set; }
    double? DataBlockIndexUtilRatio { get; set; }
    bool EnableFileWarmer { get; set; }
    double CompressibilityHint { get; set; }

    ulong BlobTransactionsDbBlockCacheSize { get; set; }

    ulong ReceiptsDbWriteBufferSize { get; set; }
    uint ReceiptsDbWriteBufferNumber { get; set; }
    ulong ReceiptsDbBlockCacheSize { get; set; }
    int? ReceiptsDbMaxOpenFiles { get; set; }
    long? ReceiptsDbMaxBytesPerSec { get; set; }
    int? ReceiptsDbBlockSize { get; set; }
    ulong ReceiptsDbTargetFileSizeBase { get; set; }
    double ReceiptsDbCompressibilityHint { get; set; }
    string? ReceiptsDbAdditionalRocksDbOptions { get; set; }

    ulong BlocksDbWriteBufferSize { get; set; }
    uint BlocksDbWriteBufferNumber { get; set; }
    ulong BlocksDbBlockCacheSize { get; set; }
    int? BlocksDbMaxOpenFiles { get; set; }
    long? BlocksDbMaxBytesPerSec { get; set; }
    int? BlocksBlockSize { get; set; }
    string? BlocksDbAdditionalRocksDbOptions { get; set; }

    ulong HeadersDbWriteBufferSize { get; set; }
    uint HeadersDbWriteBufferNumber { get; set; }
    ulong HeadersDbBlockCacheSize { get; set; }
    int? HeadersDbMaxOpenFiles { get; set; }
    long? HeadersDbMaxBytesPerSec { get; set; }
    int? HeadersDbBlockSize { get; set; }
    string? HeadersDbAdditionalRocksDbOptions { get; set; }
    ulong? HeadersDbMaxBytesForLevelBase { get; set; }

    ulong BlockNumbersDbWriteBufferSize { get; set; }
    uint BlockNumbersDbWriteBufferNumber { get; set; }
    ulong BlockNumbersDbBlockCacheSize { get; set; }
    int? BlockNumbersDbMaxOpenFiles { get; set; }
    long? BlockNumbersDbMaxBytesPerSec { get; set; }
    int? BlockNumbersDbBlockSize { get; set; }
    ulong? BlockNumbersDbRowCacheSize { get; set; }
    bool? BlockNumbersDbUseHashSkipListMemtable { get; set; }
    string? BlockNumbersDbAdditionalRocksDbOptions { get; set; }
    ulong? BlockNumbersDbMaxBytesForLevelBase { get; set; }

    ulong BlockInfosDbWriteBufferSize { get; set; }
    uint BlockInfosDbWriteBufferNumber { get; set; }
    ulong BlockInfosDbBlockCacheSize { get; set; }
    int? BlockInfosDbMaxOpenFiles { get; set; }
    long? BlockInfosDbMaxBytesPerSec { get; set; }
    int? BlockInfosDbBlockSize { get; set; }
    string? BlockInfosDbAdditionalRocksDbOptions { get; set; }

    ulong PendingTxsDbWriteBufferSize { get; set; }
    uint PendingTxsDbWriteBufferNumber { get; set; }
    ulong PendingTxsDbBlockCacheSize { get; set; }
    int? PendingTxsDbMaxOpenFiles { get; set; }
    long? PendingTxsDbMaxBytesPerSec { get; set; }
    int? PendingTxsDbBlockSize { get; set; }
    string? MetadataDbAdditionalRocksDbOptions { get; set; }

    ulong StateDbWriteBufferSize { get; set; }
    uint StateDbWriteBufferNumber { get; set; }
    ulong StateDbBlockCacheSize { get; set; }
    int? StateDbMaxOpenFiles { get; set; }
    long? StateDbMaxBytesPerSec { get; set; }
    int? StateDbBlockSize { get; set; }
    int StateDbTargetFileSizeMultiplier { get; set; }
    ulong? StateDbPrefixExtractorLength { get; set; }
    bool? StateDbVerifyChecksum { get; set; }
    double StateDbMaxBytesForLevelMultiplier { get; set; }
    ulong? StateDbMaxBytesForLevelBase { get; set; }
    int StateDbMinWriteBufferNumberToMerge { get; set; }
    ulong? StateDbRowCacheSize { get; set; }
    long? StateDbMaxWriteBufferSizeToMaintain { get; set; }
    bool StateDbUseHashSkipListMemtable { get; set; }
    int? StateDbBlockRestartInterval { get; set; }
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
