// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db.Rocks.Config;

namespace Nethermind.FlatPriorityRead;

/// <summary>Gives <see cref="RocksDbPrioritySlotStore"/>'s database its own configuration.</summary>
/// <remarks>
/// The base factory is not asked for that database: its per-database options would mix the global block-based
/// table defaults into the PlainTable setup, and debug builds reject a database name without options in
/// <see cref="IDbConfig"/>.
/// </remarks>
public sealed class FlatPriorityReadRocksDbConfigFactory(IRocksDbConfigFactory baseFactory, IFlatPriorityReadConfig config, IDbConfig dbConfig)
    : IRocksDbConfigFactory
{
    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName) =>
        databaseName == RocksDbPrioritySlotStore.DbName
            ? new FlatPriorityReadRocksDbConfig(config, dbConfig)
            : baseFactory.GetForDatabase(databaseName, columnName);
}
