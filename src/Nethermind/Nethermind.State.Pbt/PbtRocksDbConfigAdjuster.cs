// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Db.Rocks.Config;

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
/// </remarks>
internal sealed class PbtRocksDbConfigAdjuster(
    IRocksDbConfigFactory rocksDbConfigFactory,
    IDbConfig dbConfig,
    IPbtConfig pbtConfig)
    : IRocksDbConfigFactory
{
    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
    {
        if (databaseName != nameof(DbNames.Pbt)) return rocksDbConfigFactory.GetForDatabase(databaseName, columnName);

        IRocksDbConfig config = new PerTableDbConfig(dbConfig, databaseName, columnName, validate: false);
        return new AdjustedRocksdbConfig(
            config,
            pbtConfig.RocksDbOptions + ColumnRocksDbOptions(columnName),
            config.WriteBufferSize.GetValueOrDefault());
    }

    private string ColumnRocksDbOptions(string? columnName) => columnName switch
    {
        nameof(PbtColumns.Metadata) => pbtConfig.MetadataRocksDbOptions,
        nameof(PbtColumns.AccountLeaves) => pbtConfig.AccountLeavesRocksDbOptions,
        nameof(PbtColumns.CodeLeaves) => pbtConfig.CodeLeavesRocksDbOptions,
        nameof(PbtColumns.StorageLeaves) => pbtConfig.StorageLeavesRocksDbOptions,
        nameof(PbtColumns.AccountTrieNodes) => pbtConfig.AccountTrieNodesRocksDbOptions,
        nameof(PbtColumns.CodeTrieNodes) => pbtConfig.CodeTrieNodesRocksDbOptions,
        nameof(PbtColumns.StorageTrieNodes) => pbtConfig.StorageTrieNodesRocksDbOptions,
        _ => "",
    };
}
