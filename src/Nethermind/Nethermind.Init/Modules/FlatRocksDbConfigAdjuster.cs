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

            // Value columns share BlockCacheSizeBudget (tip processing and RPC read flat values constantly).
            // Trie-node columns are cached only when NodeBlockCacheSizeBudget is set: at the tip their working
            // set is served by the managed trie-node cache and snapshots, but during archive replay every
            // merkleization loads node paths from disk and those point reads dominate — replay setups opt in.
            IntPtr? cacheHandle = columnName switch
            {
                nameof(FlatDbColumns.Account) => CreateCache(columnName, flatDbConfig.BlockCacheSizeBudget, 0.3),
                nameof(FlatDbColumns.Storage) => CreateCache(columnName, flatDbConfig.BlockCacheSizeBudget, 0.7),
                nameof(FlatDbColumns.StorageNodes) => CreateCache(columnName, flatDbConfig.NodeBlockCacheSizeBudget, 0.6),
                nameof(FlatDbColumns.StateNodes) => CreateCache(columnName, flatDbConfig.NodeBlockCacheSizeBudget, 0.3),
                nameof(FlatDbColumns.StateTopNodes) => CreateCache(columnName, flatDbConfig.NodeBlockCacheSizeBudget, 0.1),
                _ => null,
            };

            config = new AdjustedRocksdbConfig(config, additionalConfig, config.WriteBufferSize.GetValueOrDefault(), cacheHandle);
        }

        return config;
    }

    private IntPtr? CreateCache(string columnName, ulong budget, double budgetShare)
    {
        if (budget == 0) return null;

        ulong cacheCapacity = (ulong)(budget * budgetShare);
        if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / 1UL.MiB):N0} MB of block cache to {columnName}");
        HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
        disposeStack.Push(cacheWrapper);
        return cacheWrapper.Handle;
    }
}
