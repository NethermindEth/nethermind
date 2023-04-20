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
    long? MaxWriteBytesPerSec { get; set; }
    int? BlockSize { get; set; }
    IDictionary<string, string>? AdditionalRocksDbOptions { get; set; }

    ulong ReceiptsDbWriteBufferSize { get; set; }
    uint ReceiptsDbWriteBufferNumber { get; set; }
    ulong ReceiptsDbBlockCacheSize { get; set; }
    bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; }
    int? ReceiptsDbMaxOpenFiles { get; set; }
    long? ReceiptsDbMaxWriteBytesPerSec { get; set; }
    int? ReceiptsBlockSize { get; set; }
    IDictionary<string, string>? ReceiptsDbAdditionalRocksDbOptions { get; set; }

    ulong BlocksDbWriteBufferSize { get; set; }
    uint BlocksDbWriteBufferNumber { get; set; }
    ulong BlocksDbBlockCacheSize { get; set; }
    bool BlocksDbCacheIndexAndFilterBlocks { get; set; }
    int? BlocksDbMaxOpenFiles { get; set; }
    long? BlocksDbMaxWriteBytesPerSec { get; set; }
    int? BlocksBlockSize { get; set; }
    IDictionary<string, string>? BlocksDbAdditionalRocksDbOptions { get; set; }

    ulong HeadersDbWriteBufferSize { get; set; }
    uint HeadersDbWriteBufferNumber { get; set; }
    ulong HeadersDbBlockCacheSize { get; set; }
    bool HeadersDbCacheIndexAndFilterBlocks { get; set; }
    int? HeadersDbMaxOpenFiles { get; set; }
    long? HeadersDbMaxWriteBytesPerSec { get; set; }
    int? HeadersBlockSize { get; set; }
    IDictionary<string, string>? HeadersDbAdditionalRocksDbOptions { get; set; }

    ulong BlockInfosDbWriteBufferSize { get; set; }
    uint BlockInfosDbWriteBufferNumber { get; set; }
    ulong BlockInfosDbBlockCacheSize { get; set; }
    bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; }
    int? BlockInfosDbMaxOpenFiles { get; set; }
    long? BlockInfosDbMaxWriteBytesPerSec { get; set; }
    int? BlockInfosBlockSize { get; set; }
    IDictionary<string, string>? BlockInfosDbAdditionalRocksDbOptions { get; set; }

    ulong PendingTxsDbWriteBufferSize { get; set; }
    uint PendingTxsDbWriteBufferNumber { get; set; }
    ulong PendingTxsDbBlockCacheSize { get; set; }
    bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; }
    int? PendingTxsDbMaxOpenFiles { get; set; }
    long? PendingTxsDbMaxWriteBytesPerSec { get; set; }
    int? PendingTxsBlockSize { get; set; }
    IDictionary<string, string>? PendingTxsDbAdditionalRocksDbOptions { get; set; }

    ulong CodeDbWriteBufferSize { get; set; }
    uint CodeDbWriteBufferNumber { get; set; }
    ulong CodeDbBlockCacheSize { get; set; }
    bool CodeDbCacheIndexAndFilterBlocks { get; set; }
    int? CodeDbMaxOpenFiles { get; set; }
    long? CodeDbMaxWriteBytesPerSec { get; set; }
    int? CodeBlockSize { get; set; }
    IDictionary<string, string>? CodeDbAdditionalRocksDbOptions { get; set; }

    ulong BloomDbWriteBufferSize { get; set; }
    uint BloomDbWriteBufferNumber { get; set; }
    ulong BloomDbBlockCacheSize { get; set; }
    bool BloomDbCacheIndexAndFilterBlocks { get; set; }
    int? BloomDbMaxOpenFiles { get; set; }
    long? BloomDbMaxWriteBytesPerSec { get; set; }
    IDictionary<string, string>? BloomDbAdditionalRocksDbOptions { get; set; }

    ulong WitnessDbWriteBufferSize { get; set; }
    uint WitnessDbWriteBufferNumber { get; set; }
    ulong WitnessDbBlockCacheSize { get; set; }
    bool WitnessDbCacheIndexAndFilterBlocks { get; set; }
    int? WitnessDbMaxOpenFiles { get; set; }
    long? WitnessDbMaxWriteBytesPerSec { get; set; }
    int? WitnessBlockSize { get; set; }
    IDictionary<string, string>? WitnessDbAdditionalRocksDbOptions { get; set; }

    ulong CanonicalHashTrieDbWriteBufferSize { get; set; }
    uint CanonicalHashTrieDbWriteBufferNumber { get; set; }
    ulong CanonicalHashTrieDbBlockCacheSize { get; set; }
    bool CanonicalHashTrieDbCacheIndexAndFilterBlocks { get; set; }
    int? CanonicalHashTrieDbMaxOpenFiles { get; set; }
    long? CanonicalHashTrieDbMaxWriteBytesPerSec { get; set; }
    int? CanonicalHashTrieBlockSize { get; set; }
    IDictionary<string, string>? CanonicalHashTrieDbAdditionalRocksDbOptions { get; set; }

    ulong MetadataDbWriteBufferSize { get; set; }
    uint MetadataDbWriteBufferNumber { get; set; }
    ulong MetadataDbBlockCacheSize { get; set; }
    bool MetadataDbCacheIndexAndFilterBlocks { get; set; }
    int? MetadataDbMaxOpenFiles { get; set; }
    long? MetadataDbMaxWriteBytesPerSec { get; set; }
    int? MetadataBlockSize { get; set; }
    IDictionary<string, string>? MetadataDbAdditionalRocksDbOptions { get; set; }

    ulong StateDbWriteBufferSize { get; set; }
    uint StateDbWriteBufferNumber { get; set; }
    ulong StateDbBlockCacheSize { get; set; }
    bool StateDbCacheIndexAndFilterBlocks { get; set; }
    int? StateDbMaxOpenFiles { get; set; }
    long? StateDbMaxWriteBytesPerSec { get; set; }
    int? StateDbBlockSize { get; set; }
    IDictionary<string, string>? StateDbAdditionalRocksDbOptions { get; set; }

    /// <summary>
    /// Enables DB Statistics - https://github.com/facebook/rocksdb/wiki/Statistics
    /// It can has a RocksDB perfomance hit between 5 and 10%.
    /// </summary>
    bool EnableDbStatistics { get; set; }
    bool EnableMetricsUpdater { get; set; }
    /// <summary>
    /// If not zero, dump rocksdb.stats to LOG every stats_dump_period_sec
    /// Default: 600 (10 min)
    /// </summary>
    uint StatsDumpPeriodSec { get; set; }
}
