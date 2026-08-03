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
    IHardwareInfo hardwareInfo,
    IDisposableStack disposeStack,
    ILogManager logManager)
    : IRocksDbConfigFactory
{
    private const int AutoScaleMemoryDivisor = 8;
    private static readonly ulong AutoScaleFloor = 1UL.GiB;
    private static readonly ulong AutoScaleCap = 32UL.GiB;

    private readonly ILogger _logger = logManager.GetClassLogger<FlatRocksDbConfigAdjuster>();
    private ulong? _blockCacheSizeBudget;

    internal ulong BlockCacheSizeBudget => _blockCacheSizeBudget ??= ResolveBlockCacheSizeBudget();

    private ulong ResolveBlockCacheSizeBudget()
    {
        ulong totalMemory = (ulong)Math.Max(hardwareInfo.AvailableMemoryBytes, 0);
        ulong configured = flatDbConfig.BlockCacheSizeBudget;
        if (configured != 0)
        {
            if (configured > totalMemory / 2 && _logger.IsWarn)
                _logger.Warn($"Flat db block cache budget of {configured / 1UL.MiB:N0} MB exceeds half of the {totalMemory / 1UL.MiB:N0} MB of system memory");
            return configured;
        }

        ulong budget = Math.Clamp(totalMemory / AutoScaleMemoryDivisor, AutoScaleFloor, AutoScaleCap);
        if (_logger.IsInfo) _logger.Info($"Auto-scaled flat db block cache budget to {budget / 1UL.MiB:N0} MB based on {totalMemory / 1UL.MiB:N0} MB of system memory");
        return budget;
    }

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
                ulong cacheCapacity = (ulong)(BlockCacheSizeBudget * 0.3);
                if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / 1UL.MiB):N0} MB of block cache to account");
                HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
                cacheHandle = cacheWrapper.Handle;
                disposeStack.Push(cacheWrapper);
            }

            if (columnName == nameof(FlatDbColumns.Storage))
            {
                ulong cacheCapacity = (ulong)(BlockCacheSizeBudget * 0.7);
                if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / 1UL.MiB):N0} MB of block cache to storage");
                HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
                cacheHandle = cacheWrapper.Handle;
                disposeStack.Push(cacheWrapper);
            }

            config = new AdjustedRocksdbConfig(config, additionalConfig, config.WriteBufferSize.GetValueOrDefault(), cacheHandle);
        }

        return config;
    }
}
