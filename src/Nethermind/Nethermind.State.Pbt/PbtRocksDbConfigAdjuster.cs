// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.State.Pbt;

/// <summary>
/// Supplies the rocksdb config of the pbt database and its columns.
/// </summary>
/// <remarks>
/// <see cref="PerTableDbConfig"/> resolves per-column options from <see cref="IDbConfig"/> by naming
/// convention, and the pbt columns have no entries there: they live in <see cref="IPbtConfig"/>, so
/// the backend that owns them owns their tuning too. The per-table config is therefore built with
/// validation off, and the pbt options appended to it - the column's last, so that it wins over the
/// shared one, which in turn wins over the global database options.
/// Like the flat account and storage columns, <see cref="PbtColumns.Accounts"/> and
/// <see cref="PbtColumns.Storages"/> get their own block caches carved out of
/// <see cref="IPbtConfig.BlockCacheSizeBudget"/> instead of competing in the shared one.
/// </remarks>
internal sealed class PbtRocksDbConfigAdjuster(
    IRocksDbConfigFactory rocksDbConfigFactory,
    IDbConfig dbConfig,
    IPbtConfig pbtConfig,
    IDisposableStack disposeStack,
    ILogManager logManager)
    : IRocksDbConfigFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRocksDbConfigAdjuster>();

    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
    {
        if (databaseName != nameof(DbNames.Pbt)) return rocksDbConfigFactory.GetForDatabase(databaseName, columnName);

        IRocksDbConfig config = new PerTableDbConfig(dbConfig, databaseName, columnName, validate: false);
        return new AdjustedRocksdbConfig(
            config,
            pbtConfig.RocksDbOptions + ColumnRocksDbOptions(columnName),
            config.WriteBufferSize.GetValueOrDefault(),
            DedicatedBlockCache(columnName));
    }

    private string ColumnRocksDbOptions(string? columnName) => columnName switch
    {
        nameof(PbtColumns.Accounts) => pbtConfig.AccountsRocksDbOptions,
        nameof(PbtColumns.Storages) => pbtConfig.StoragesRocksDbOptions,
        nameof(PbtColumns.Codes) => pbtConfig.CodesRocksDbOptions,
        nameof(PbtColumns.Metadata) => pbtConfig.MetadataRocksDbOptions,
        nameof(PbtColumns.AccountNodeGroups) or nameof(PbtColumns.CodeNodeGroups) or nameof(PbtColumns.StorageNodeGroups)
            => pbtConfig.NodeGroupsRocksDbOptions,
        _ => "",
    };

    private nint? DedicatedBlockCache(string? columnName)
    {
        double share = columnName switch
        {
            nameof(PbtColumns.Accounts) => 0.3,
            nameof(PbtColumns.Storages) => 0.7,
            _ => 0,
        };
        if (share == 0) return null;

        ulong cacheCapacity = (ulong)(pbtConfig.BlockCacheSizeBudget * share);
        if (_logger.IsInfo) _logger.Info($"Setting {(cacheCapacity / 1UL.MiB):N0} MB of block cache to {columnName}");
        HyperClockCacheWrapper cacheWrapper = new(cacheCapacity);
        disposeStack.Push(cacheWrapper);
        return cacheWrapper.Handle;
    }
}
