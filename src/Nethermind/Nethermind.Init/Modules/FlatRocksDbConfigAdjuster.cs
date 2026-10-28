// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Init.Modules;

/// <summary>
/// Adjust rocksdb config depending on the flatdb config
/// </summary>
internal class FlatRocksDbConfigAdjuster(
    IRocksDbConfigFactory rocksDbConfigFactory,
    IFlatDbConfig flatDbConfig,
    IDisposableStack disposeStack,
    ILogManager logManager)
    : IRocksDbConfigFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatRocksDbConfigAdjuster>();
    private nint? _historyCacheHandle;

    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
    {
        IRocksDbConfig config = rocksDbConfigFactory.GetForDatabase(databaseName, columnName);
        if (databaseName == nameof(DbNames.Flat))
        {
            string additionalConfig = "";
            if (flatDbConfig.Layout == FlatLayout.FlatInTrie)
            {
                // For flat in trie, add optimize filter for hits and turn on partitioned index, this reduces
                // memory at expense of latency.
                additionalConfig = config.RocksDbOptions +
                                   "optimize_filters_for_hits=true;" +
                                   "block_based_table_factory.partition_filters=true;" +
                                   "block_based_table_factory.index_type=kTwoLevelIndexSearch;";
            }

            IntPtr? cacheHandle = null;
            if (columnName == nameof(FlatDbColumns.Account))
            {
                ulong cacheCapacity = (ulong)(flatDbConfig.BlockCacheSizeBudget * 0.3);
                if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / 1UL.MiB):N0} MB of block cache to account");
                HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
                cacheHandle = cacheWrapper.Handle;
                disposeStack.Push(cacheWrapper);
            }

            if (columnName == nameof(FlatDbColumns.Storage))
            {
                ulong cacheCapacity = (ulong)(flatDbConfig.BlockCacheSizeBudget * 0.7);
                if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / 1UL.MiB):N0} MB of block cache to storage");
                HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
                cacheHandle = cacheWrapper.Handle;
                disposeStack.Push(cacheWrapper);
            }

            config = new AdjustedRocksdbConfig(config, additionalConfig, config.WriteBufferSize.GetValueOrDefault(), cacheHandle);
        }

        if (databaseName == nameof(DbNames.FlatHistory))
        {
            nint? cacheHandle = GetHistoryCacheHandle(columnName);
            bool compactOnDeletions = flatDbConfig.HistoryRetention == HistoryRetentionMode.Rolling;
            if (cacheHandle is not null || compactOnDeletions)
            {
                config = new AdjustedRocksdbConfig(config, "", config.WriteBufferSize.GetValueOrDefault(), cacheHandle, compactOnDeletions);
            }
        }

        return config;
    }

    // As-of reads seek AccountHistory and StorageHistory, and every storage read can also seek StorageClears.
    private nint? GetHistoryCacheHandle(string? columnName)
    {
        if (!flatDbConfig.HistoryEnabled || flatDbConfig.HistoryBlockCacheSize == 0) return null;
        if (columnName != nameof(FlatHistoryColumns.AccountHistory) &&
            columnName != nameof(FlatHistoryColumns.StorageHistory) &&
            columnName != nameof(FlatHistoryColumns.StorageClears)) return null;

        if (_historyCacheHandle is null)
        {
            ulong cacheCapacity = flatDbConfig.HistoryBlockCacheSize;
            if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / 1UL.MiB):N0} MB of block cache to history");
            HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
            _historyCacheHandle = cacheWrapper.Handle;
            disposeStack.Push(cacheWrapper);
        }

        return _historyCacheHandle;
    }
}
