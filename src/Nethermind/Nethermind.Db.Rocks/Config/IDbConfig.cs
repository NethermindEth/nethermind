// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db.Rocks.Config;

[ConfigCategory(HiddenFromDocs = true)]
public interface IDbConfig : IConfig
{
    ulong SharedBlockCacheSize { get; set; }
    public bool SkipMemoryHintSetting { get; set; }

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


    int? MaxOpenFiles { get; set; }
    bool WriteAheadLogSync { get; set; }
    ulong? ReadAheadSize { get; set; }
    string RocksDbOptions { get; set; }
    string? AdditionalRocksDbOptions { get; set; }
    bool? VerifyChecksum { get; set; }
    bool EnableFileWarmer { get; set; }
    double CompressibilityHint { get; set; }
    bool FlushOnExit { get; set; }

    string BadBlocksDbRocksDbOptions { get; set; }
    string? BadBlocksDbAdditionalRocksDbOptions { get; set; }

    string BlobTransactionsDbRocksDbOptions { get; set; }
    string? BlobTransactionsDbAdditionalRocksDbOptions { get; set; }

    string BlobTransactionsFullBlobTxsDbRocksDbOptions { get; set; }
    string? BlobTransactionsFullBlobTxsDbAdditionalRocksDbOptions { get; set; }
    string BlobTransactionsLightBlobTxsDbRocksDbOptions { get; set; }
    string? BlobTransactionsLightBlobTxsDbAdditionalRocksDbOptions { get; set; }
    string BlobTransactionsProcessedTxsDbRocksDbOptions { get; set; }
    string? BlobTransactionsProcessedTxsDbAdditionalRocksDbOptions { get; set; }

    double ReceiptsDbCompressibilityHint { get; set; }
    string ReceiptsDbRocksDbOptions { get; set; }
    string? ReceiptsDbAdditionalRocksDbOptions { get; set; }
    string ReceiptsDefaultDbRocksDbOptions { get; set; }
    string? ReceiptsDefaultDbAdditionalRocksDbOptions { get; set; }
    string ReceiptsTransactionsDbRocksDbOptions { get; set; }
    string? ReceiptsTransactionsDbAdditionalRocksDbOptions { get; set; }
    string ReceiptsBlocksDbRocksDbOptions { get; set; }
    string? ReceiptsBlocksDbAdditionalRocksDbOptions { get; set; }

    string BlocksDbRocksDbOptions { get; set; }
    string? BlocksDbAdditionalRocksDbOptions { get; set; }

    string HeadersDbRocksDbOptions { get; set; }
    string? HeadersDbAdditionalRocksDbOptions { get; set; }

    ulong? BlockNumbersDbRowCacheSize { get; set; }
    string BlockNumbersDbRocksDbOptions { get; set; }
    string? BlockNumbersDbAdditionalRocksDbOptions { get; set; }

    string BlockInfosDbRocksDbOptions { get; set; }
    string? BlockInfosDbAdditionalRocksDbOptions { get; set; }

    string PendingTxsDbRocksDbOptions { get; set; }
    string? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    string MetadataDbRocksDbOptions { get; set; }
    string? MetadataDbAdditionalRocksDbOptions { get; set; }

    string BloomDbRocksDbOptions { get; set; }
    string? BloomDbAdditionalRocksDbOptions { get; set; }

    ulong? CodeDbRowCacheSize { get; set; }
    string CodeDbRocksDbOptions { get; set; }
    string? CodeDbAdditionalRocksDbOptions { get; set; }


    [ConfigItem(Description = "Write buffer size for state db. This should be at least 20% of pruning cache or during persist, persist is not able to be done asynchronously.")]
    ulong StateDbWriteBufferSize { get; set; }
    ulong StateDbWriteBufferNumber { get; set; }
    bool? StateDbVerifyChecksum { get; set; }
    ulong? StateDbRowCacheSize { get; set; }
    bool StateDbEnableFileWarmer { get; set; }
    double StateDbCompressibilityHint { get; set; }
    string StateDbRocksDbOptions { get; set; }
    string? StateDbAdditionalRocksDbOptions { get; set; }
    string StateDbLargeMemoryRocksDbOptions { get; set; }
    string StateDbArchiveModeRocksDbOptions { get; set; }
    ulong StateDbLargeMemoryWriteBufferSize { get; set; }
    ulong StateDbArchiveModeWriteBufferSize { get; set; }


    string L1OriginDbRocksDbOptions { get; set; }
    string? L1OriginDbAdditionalRocksDbOptions { get; set; }
}
