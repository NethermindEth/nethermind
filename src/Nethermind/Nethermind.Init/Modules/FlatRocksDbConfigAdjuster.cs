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

            ulong cacheCapacity = GetColumnBlockCacheCapacity(
                flatDbConfig.BlockCacheSizeBudget,
                columnName,
                flatDbConfig.BlockCacheAccountShare,
                flatDbConfig.BlockCacheStorageShare,
                flatDbConfig.BlockCacheStorageNodesShare);
            IntPtr? cacheHandle = null;
            if (cacheCapacity != 0)
            {
                if (columnName == nameof(FlatDbColumns.StorageNodes))
                {
                    if (!HasBlockCacheOption(config))
                    {
                        if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / (ulong)1.MiB):N0} MB of block cache to {columnName}");
                        additionalConfig += $"{BlockCacheOptionName}={cacheCapacity};";
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / (ulong)1.MiB):N0} MB of block cache to {columnName}");
                    HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
                    cacheHandle = cacheWrapper.Handle;
                    disposeStack.Push(cacheWrapper);
                }
            }

            config = new AdjustedRocksdbConfig(config, additionalConfig, config.WriteBufferSize.GetValueOrDefault(), cacheHandle);
        }

        return config;
    }

    internal static ulong GetColumnBlockCacheCapacity(
        long blockCacheSizeBudget,
        string? columnName,
        long accountShare = 10,
        long storageShare = 25,
        long storageNodesShare = 65)
    {
        if (blockCacheSizeBudget <= 0)
        {
            return 0;
        }

        accountShare = Math.Max(0, accountShare);
        storageShare = Math.Max(0, storageShare);
        storageNodesShare = Math.Max(0, storageNodesShare);

        long totalShare = accountShare + storageShare + storageNodesShare;
        if (totalShare == 0)
        {
            return 0;
        }

        long budgetShare = columnName switch
        {
            nameof(FlatDbColumns.Account) => accountShare,
            nameof(FlatDbColumns.Storage) => storageShare,
            nameof(FlatDbColumns.StorageNodes) => storageNodesShare,
            _ => 0,
        };

        return (ulong)(blockCacheSizeBudget * budgetShare / totalShare);
    }

    private static bool HasBlockCacheOption(IRocksDbConfig config) =>
        DbOnTheRocks.ExtractOptions(config.RocksDbOptions + config.AdditionalRocksDbOptions).ContainsKey(BlockCacheOptionName);
}
