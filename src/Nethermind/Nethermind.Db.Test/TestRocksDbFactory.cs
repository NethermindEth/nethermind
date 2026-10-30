// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Testably.Abstractions;

namespace Nethermind.Db.Test;

/// <summary>A <see cref="RocksDbFactory"/> whose open, repair and fatal shutdown tests can observe or fail.</summary>
internal class TestRocksDbFactory(IRocksDbConfigFactory rocksDbConfigFactory, IDbConfig dbConfig, ILogManager logManager, string basePath, IFileSystem fileSystem)
    : RocksDbFactory(rocksDbConfigFactory, dbConfig, new HyperClockCacheWrapper(), fileSystem, logManager, basePath)
{
    public string? OpenExceptionMessage { get; init; }
    public Action? OnFatalShutdown { get; init; }
    public Action? OnRepair { get; init; }

    public static DbOnTheRocks OpenDb(string basePath, DbSettings settings, IDbConfig dbConfig, IRocksDbConfigFactory rocksDbConfigFactory, ILogManager logManager) =>
        (DbOnTheRocks)new TestRocksDbFactory(rocksDbConfigFactory, dbConfig, logManager, basePath, new RealFileSystem()).CreateDb(settings);

    public static ColumnsDb<T> OpenColumnsDb<T>(string basePath, DbSettings settings, IDbConfig dbConfig, IRocksDbConfigFactory rocksDbConfigFactory) where T : struct, Enum =>
        (ColumnsDb<T>)new TestRocksDbFactory(rocksDbConfigFactory, dbConfig, LimboLogs.Instance, basePath, new RealFileSystem()).CreateColumnsDb<T>(settings);

    protected override RocksDb DoOpen(string path, DbOptions options, ColumnFamilies? families) =>
        OpenExceptionMessage is null ? base.DoOpen(path, options, families) : throw new RocksDbException(OpenExceptionMessage);

    protected override void FatalShutdown()
    {
        if (OnFatalShutdown is null) base.FatalShutdown();
        else OnFatalShutdown();
    }

    protected override void RepairDb(DbOptions dbOptions, string path)
    {
        if (OnRepair is null) base.RepairDb(dbOptions, path);
        else OnRepair();
    }
}
