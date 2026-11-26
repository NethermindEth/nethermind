// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Rocks.Config;

public interface IRocksDbConfigFactory
{
    /// <summary>Gets database options, including global defaults and resource adjustments.</summary>
    /// <param name="databaseName">The database name, or <see langword="null"/> for global-only defaults without per-table options.</param>
    /// <param name="columnName">The column name. Ignored when <paramref name="databaseName"/> is null.</param>
    IRocksDbConfig GetForDatabase(string? databaseName, string? columnName);
}
