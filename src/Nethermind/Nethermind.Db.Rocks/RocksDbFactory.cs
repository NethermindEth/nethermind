// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

public class RocksDbFactory : IRocksDbFactory
{
    private readonly IDbConfig _dbConfig;

    private readonly ILogManager _logManager;

    private readonly string _basePath;

    public RocksDbFactory(IDbConfig dbConfig, ILogManager logManager, string basePath)
    {
        _dbConfig = dbConfig;
        _logManager = logManager;
        _basePath = basePath;
    }

    public IDb CreateDb(RocksDbSettings rocksDbSettings) =>
        new DbOnTheRocks(_basePath, rocksDbSettings, _dbConfig, _logManager);

    public IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings) where T : struct, Enum =>
        new ColumnsDb<T>(_basePath, rocksDbSettings, _dbConfig, _logManager, Array.Empty<T>());

    public string GetFullDbPath(RocksDbSettings rocksDbSettings) => DbOnTheRocks.GetFullDbPath(rocksDbSettings.DbPath, _basePath);
}
