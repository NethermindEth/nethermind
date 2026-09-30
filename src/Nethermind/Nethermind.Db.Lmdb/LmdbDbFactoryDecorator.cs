// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Db.Lmdb;

/// <summary>
/// Decorates the registered <see cref="IDbFactory"/> so that the FlatDb Account/Storage columns are backed by
/// LMDB instead of RocksDB, while every other database (including the rest of the FlatDb columns) is created
/// through the decorated factory unchanged - see PLAN-LMDB.md "Architecture".
/// </summary>
public sealed class LmdbDbFactoryDecorator(IDbFactory innerFactory, ILmdbConfig config, ILogManager logManager) : IDbFactory
{
    public IDb CreateDb(DbSettings dbSettings) => innerFactory.CreateDb(dbSettings);

    public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
    {
        // ContainerBuilderExtensions.AddColumnDatabase<T> title-cases the dbName it passes through DbSettings.DbName
        // (see Nethermind.Init.Modules.ContainerBuilderExtensions.GetTitleDbName), so DbNames.Flat ("flat") arrives
        // here as "Flat" - compare case-insensitively rather than depending on that casing.
        if (typeof(T) != typeof(FlatDbColumns) || !string.Equals(dbSettings.DbName, DbNames.Flat, StringComparison.OrdinalIgnoreCase))
        {
            return innerFactory.CreateColumnsDb<T>(dbSettings);
        }

        IColumnsDb<T> rocksColumnsDb = innerFactory.CreateColumnsDb<T>(dbSettings);
        string basePath = innerFactory.GetFullDbPath(dbSettings);
        LmdbDb account = new("LmdbAccount", Path.Combine(basePath, "account.lmdb"), config.InitialMapSizeBytes, config.MaxReaders, config.MaxOperationsPerWave, logManager);
        LmdbDb storage = new("LmdbStorage", Path.Combine(basePath, "storage.lmdb"), config.InitialMapSizeBytes, config.MaxReaders, config.MaxOperationsPerWave, logManager);

        // T has just been confirmed to be FlatDbColumns above, so these casts merely tell the compiler what the
        // runtime already knows; IColumnsDb<T> cannot be constructed generically from a non-generic adapter type.
        IColumnsDb<FlatDbColumns> adapter = new LmdbColumnsDbAdapter((IColumnsDb<FlatDbColumns>)(object)rocksColumnsDb, account, storage, logManager);
        return (IColumnsDb<T>)(object)adapter;
    }

    public string GetFullDbPath(DbSettings dbSettings) => innerFactory.GetFullDbPath(dbSettings);
}
