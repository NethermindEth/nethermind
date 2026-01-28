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
                if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / (ulong)1.MiB()):N0} MB of block cache to account");
                HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
                cacheHandle = cacheWrapper.Handle;
                disposeStack.Push(cacheWrapper);
            }

            if (columnName == nameof(FlatDbColumns.Storage))
            {
                ulong cacheCapacity = (ulong)(flatDbConfig.BlockCacheSizeBudget * 0.7);
                if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / (ulong)1.MiB()):N0} MB of block cache to storage");
                HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
                cacheHandle = cacheWrapper.Handle;
                disposeStack.Push(cacheWrapper);
            }

            config = new AdjustedRocksdbConfig(config, additionalConfig, config.WriteBufferSize.GetValueOrDefault(), cacheHandle);
        }

        return config;
    }
}
