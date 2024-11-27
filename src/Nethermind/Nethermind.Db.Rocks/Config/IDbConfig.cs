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


    ulong WriteBufferSize { get; set; }
    uint WriteBufferNumber { get; set; }
    int? MaxOpenFiles { get; set; }
    bool WriteAheadLogSync { get; set; }
    ulong? ReadAheadSize { get; set; }
    string RocksDbOptions { get; set; }
    string? AdditionalRocksDbOptions { get; set; }
    bool? VerifyChecksum { get; set; }
    ulong? RowCacheSize { get; set; }
    bool EnableFileWarmer { get; set; }
    double CompressibilityHint { get; set; }

    string BlobTransactionsDbRocksDbOptions { get; set; }
    string? BlobTransactionsDbAdditionalRocksDbOptions { get; set; }

    ulong ReceiptsDbWriteBufferSize { get; set; }
    double ReceiptsDbCompressibilityHint { get; set; }
    string ReceiptsDbRocksDbOptions { get; set; }
    string? ReceiptsDbAdditionalRocksDbOptions { get; set; }

    ulong BlocksDbWriteBufferSize { get; set; }
    string BlocksDbRocksDbOptions { get; set; }
    string? BlocksDbAdditionalRocksDbOptions { get; set; }

    ulong HeadersDbWriteBufferSize { get; set; }
    string HeadersDbRocksDbOptions { get; set; }
    string? HeadersDbAdditionalRocksDbOptions { get; set; }

    ulong BlockNumbersDbWriteBufferSize { get; set; }
    ulong? BlockNumbersDbRowCacheSize { get; set; }
    string BlockNumbersDbRocksDbOptions { get; set; }
    string? BlockNumbersDbAdditionalRocksDbOptions { get; set; }

    ulong BlockInfosDbWriteBufferSize { get; set; }
    string BlockInfosDbRocksDbOptions { get; set; }
    string? BlockInfosDbAdditionalRocksDbOptions { get; set; }

    ulong PendingTxsDbWriteBufferSize { get; set; }
    string PendingTxsDbRocksDbOptions { get; set; }
    string? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    ulong MetadataDbWriteBufferSize { get; set; }
    string MetadataDbRocksDbOptions { get; set; }
    string? MetadataDbAdditionalRocksDbOptions { get; set; }

    ulong BloomDbWriteBufferSize { get; set; }
    string BloomDbRocksDbOptions { get; set; }
    string? BloomDbAdditionalRocksDbOptions { get; set; }

    ulong CodeDbWriteBufferSize { get; set; }
    ulong? CodeDbRowCacheSize { get; set; }
    string CodeDbRocksDbOptions { get; set; }
    string? CodeDbAdditionalRocksDbOptions { get; set; }


    ulong StateDbWriteBufferSize { get; set; }
    uint StateDbWriteBufferNumber { get; set; }
    bool? StateDbVerifyChecksum { get; set; }
    ulong? StateDbRowCacheSize { get; set; }
    bool StateDbEnableFileWarmer { get; set; }
    double StateDbCompressibilityHint { get; set; }
    string StateDbRocksDbOptions { get; set; }
    string? StateDbAdditionalRocksDbOptions { get; set; }
}
