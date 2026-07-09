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

    [ConfigItem(Description = "Store RocksDB index and filter blocks in the block cache (partitioned: two-level index + partitioned filter, top level pinned) instead of pinning them whole in unbounded table-reader memory. Bounds native memory at large state, where index+filter blocks across many SST files can reach tens of GB and grow with the file count. Partitioning applies to newly written/compacted SSTs (existing files read transparently) and evicts at partition granularity, so it avoids the whole-filter re-read of monolithic caching. Best for read-serving/RPC replica nodes; a head-following node still benefits from a per-column block_cache sized to hold the working set. Default off.", DefaultValue = "false")]
    bool CacheIndexAndFilterBlocks { get; set; }
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

    string? PreimageDbRocksDbOptions { get; set; }
    public string? PreimageDbAdditionalRocksDbOptions { get; set; }
}
