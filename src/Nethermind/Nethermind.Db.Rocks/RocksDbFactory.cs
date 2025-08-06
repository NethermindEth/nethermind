// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

public class RocksDbFactory : IDbFactory
{
    private readonly IDbConfig _dbConfig;

    private readonly ILogManager _logManager;

    private readonly string _basePath;

    private readonly IntPtr _sharedCache;
    private readonly IRocksDbConfigFactory _rocksDbConfigFactory;

    public RocksDbFactory(IRocksDbConfigFactory rocksDbConfigFactory, IDbConfig dbConfig, IInitConfig initConfig, ILogManager logManager)
        : this(rocksDbConfigFactory, dbConfig, logManager, initConfig.BaseDbPath)
    {

    }

    public RocksDbFactory(IRocksDbConfigFactory rocksDbConfigFactory, IDbConfig dbConfig, ILogManager logManager, string basePath)
    {
        _rocksDbConfigFactory = rocksDbConfigFactory;
        _dbConfig = dbConfig;
        _logManager = logManager;
        _basePath = basePath;

        ILogger logger = _logManager.GetClassLogger<RocksDbFactory>();

        if (logger.IsDebug)
        {
            logger.Debug($"Shared memory size is {dbConfig.SharedBlockCacheSize}");
        }

        _sharedCache = RocksDbSharp.Native.Instance.rocksdb_cache_create_lru(new UIntPtr(dbConfig.SharedBlockCacheSize));
    }

    public IDb CreateDb(DbSettings dbSettings) =>
        new DbOnTheRocks(_basePath, dbSettings, _dbConfig, _rocksDbConfigFactory, _logManager, sharedCache: _sharedCache);

    public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum =>
        new ColumnsDb<T>(_basePath, dbSettings, _dbConfig, _rocksDbConfigFactory, _logManager, Array.Empty<T>(), sharedCache: _sharedCache);

    public string GetFullDbPath(DbSettings dbSettings) => DbOnTheRocks.GetFullDbPath(dbSettings.DbPath, _basePath);
}
