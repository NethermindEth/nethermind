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
    private const long AccountBlockCacheBudgetShare = 15;
    private const long StorageBlockCacheBudgetShare = 25;
    private const long StateTopNodesBlockCacheBudgetShare = 5;
    private const long StateNodesBlockCacheBudgetShare = 15;
    private const long StorageNodesBlockCacheBudgetShare = 25;
    private const long FallbackNodesBlockCacheBudgetShare = 15;
    private const long TotalBlockCacheBudgetShare = 100;
    private const string BlockCacheOptionName = "block_based_table_factory.block_cache";

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

            ulong cacheCapacity = GetColumnBlockCacheCapacity(flatDbConfig.BlockCacheSizeBudget, columnName);
            IntPtr? cacheHandle = null;
            if (cacheCapacity != 0 && !HasBlockCacheOption(config))
            {
                if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / (ulong)1.MiB):N0} MB of block cache to {columnName}");
                HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
                cacheHandle = cacheWrapper.Handle;
                disposeStack.Push(cacheWrapper);
            }

            config = new AdjustedRocksdbConfig(config, additionalConfig, config.WriteBufferSize.GetValueOrDefault(), cacheHandle);
        }

        return config;
    }

    internal static ulong GetColumnBlockCacheCapacity(long blockCacheSizeBudget, string? columnName)
    {
        if (blockCacheSizeBudget <= 0)
        {
            return 0;
        }

        long budgetShare = columnName switch
        {
            nameof(FlatDbColumns.Account) => AccountBlockCacheBudgetShare,
            nameof(FlatDbColumns.Storage) => StorageBlockCacheBudgetShare,
            nameof(FlatDbColumns.StateTopNodes) => StateTopNodesBlockCacheBudgetShare,
            nameof(FlatDbColumns.StateNodes) => StateNodesBlockCacheBudgetShare,
            nameof(FlatDbColumns.StorageNodes) => StorageNodesBlockCacheBudgetShare,
            nameof(FlatDbColumns.FallbackNodes) => FallbackNodesBlockCacheBudgetShare,
            _ => 0,
        };

        return (ulong)(blockCacheSizeBudget * budgetShare / TotalBlockCacheBudgetShare);
    }

    private static bool HasBlockCacheOption(IRocksDbConfig config) =>
        DbOnTheRocks.ExtractOptions(config.RocksDbOptions + config.AdditionalRocksDbOptions).ContainsKey(BlockCacheOptionName);
}
