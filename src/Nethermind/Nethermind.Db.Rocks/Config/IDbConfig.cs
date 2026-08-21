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
    bool? SkipCheckingSstFileSizesOnDbOpen { get; set; }
    bool WriteAheadLogSync { get; set; }
    ulong? ReadAheadSize { get; set; }
    string RocksDbOptions { get; set; }
    string? AdditionalRocksDbOptions { get; set; }
    bool? VerifyChecksum { get; set; }
    bool EnableFileWarmer { get; set; }
    double CompressibilityHint { get; set; }
    [ConfigItem(
        Description = "How RocksDB is flushed on shutdown. 'None' skips flushing; 'WalOnly' flushes only the write-ahead log (fast, recovered via WAL replay on restart); 'Full' also materializes memtables into SST files (slower).",
        DefaultValue = "WalOnly")]
    FlushOnExitMode FlushOnExit { get; set; }

    string BadBlocksDbRocksDbOptions { get; set; }
    string? BadBlocksDbAdditionalRocksDbOptions { get; set; }

    string BlockAccessListsDbRocksDbOptions { get; set; }
    string? BlockAccessListsDbAdditionalRocksDbOptions { get; set; }

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

    string LogIndexStorageDbRocksDbOptions { get; set; }
    string LogIndexStorageDbAdditionalRocksDbOptions { get; set; }
    string LogIndexStorageMetaDbRocksDbOptions { get; set; }
    string LogIndexStorageMetaDbAdditionalRocksDbOptions { get; set; }
    string LogIndexStorageAddressesDbRocksDbOptions { get; set; }
    string LogIndexStorageAddressesDbAdditionalRocksDbOptions { get; set; }
    string LogIndexStorageTopics0DbRocksDbOptions { get; set; }
    string LogIndexStorageTopics0DbAdditionalRocksDbOptions { get; set; }
    string LogIndexStorageTopics1DbRocksDbOptions { get; set; }
    string LogIndexStorageTopics1DbAdditionalRocksDbOptions { get; set; }
    string LogIndexStorageTopics2DbRocksDbOptions { get; set; }
    string LogIndexStorageTopics2DbAdditionalRocksDbOptions { get; set; }
    string LogIndexStorageTopics3DbRocksDbOptions { get; set; }
    string LogIndexStorageTopics3DbAdditionalRocksDbOptions { get; set; }

    bool? FlatDbVerifyChecksum { get; set; }

    /// <summary>
    /// Runs one forced full compaction of the flat Account and Storage columns during startup, before block
    /// processing begins.
    /// </summary>
    /// <remarks>
    /// RocksDB applies table-format and compression options to newly written SSTs only, so an existing database keeps
    /// its old encoding indefinitely after such an option changes. A forced compaction (bottommost level included)
    /// rewrites every SST and makes the new options take effect. Off by default: on a mainnet-sized flat database
    /// this rewrites tens of gigabytes and takes hours, and it delays the node becoming ready.
    /// </remarks>
    [ConfigItem(
        Description = "Run one forced full compaction (bottommost level included) of the flat Account and Storage columns at startup, so an existing database adopts RocksDB options that only apply to newly written SSTs. Takes hours on a mainnet-sized database.",
        DefaultValue = "false")]
    bool FlatDbForceCompactOnStart { get; set; }

    string FlatDbRocksDbOptions { get; set; }
    string? FlatDbAdditionalRocksDbOptions { get; set; }

    string? FlatMetadataDbRocksDbOptions { get; set; }
    string? FlatMetadataDbAdditionalRocksDbOptions { get; set; }

    string? FlatAccountDbRocksDbOptions { get; set; }
    string? FlatAccountDbAdditionalRocksDbOptions { get; set; }

    string? FlatStorageDbRocksDbOptions { get; set; }
    string? FlatStorageDbAdditionalRocksDbOptions { get; set; }

    string? FlatStateNodesDbRocksDbOptions { get; set; }
    string? FlatStateNodesDbAdditionalRocksDbOptions { get; set; }

    string? FlatStateTopNodesDbRocksDbOptions { get; set; }
    string? FlatStateTopNodesDbAdditionalRocksDbOptions { get; set; }

    string? FlatStorageNodesDbRocksDbOptions { get; set; }
    string? FlatStorageNodesDbAdditionalRocksDbOptions { get; set; }

    string? FlatFallbackNodesDbRocksDbOptions { get; set; }
    string? FlatFallbackNodesDbAdditionalRocksDbOptions { get; set; }

    /// <summary>
    /// RocksDB options for every column of the flatHistory database (as-of-block state history). Defaults to LZ4
    /// compression, 256 MB write buffers sized for the from-genesis capture replay, and
    /// <c>optimize_filters_for_hits</c> — as-of reads are iterator floor-seeks that never consult the point bloom,
    /// whose last-level memory cost would be prohibitive on a full archive.
    /// </summary>
    string FlatHistoryDbRocksDbOptions { get; set; }

    /// <summary>Options appended after <see cref="FlatHistoryDbRocksDbOptions"/> (later keys win). Unset by default.</summary>
    string? FlatHistoryDbAdditionalRocksDbOptions { get; set; }

    /// <summary>
    /// Options appended after <see cref="FlatHistoryDbRocksDbOptions"/> for the AvailableBlocks column (per-block
    /// availability markers and the watermark). Defaults to 8 MB write buffers: the column is tiny, so the
    /// replay-sized buffers of the two bulky value columns would be wasted on it.
    /// </summary>
    string? FlatHistoryAvailableBlocksDbRocksDbOptions { get; set; }

    /// <summary>
    /// Options appended after <see cref="FlatHistoryDbRocksDbOptions"/> for the StorageClears column (per-block
    /// storage-clear markers for self-destructed accounts). Defaults to 8 MB write buffers, like
    /// <see cref="FlatHistoryAvailableBlocksDbRocksDbOptions"/>.
    /// </summary>
    string? FlatHistoryStorageClearsDbRocksDbOptions { get; set; }

    string? PreimageDbRocksDbOptions { get; set; }
    public string? PreimageDbAdditionalRocksDbOptions { get; set; }

    string? PersistedSnapshotCatalogDbRocksDbOptions { get; set; }
    string? PersistedSnapshotCatalogDbAdditionalRocksDbOptions { get; set; }
}
