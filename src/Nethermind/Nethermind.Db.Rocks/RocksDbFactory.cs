// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

    private IntPtr _sharedCache;

    public RocksDbFactory(IDbConfig dbConfig, ILogManager logManager, string basePath)
    {
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

    public IDb CreateDb(RocksDbSettings rocksDbSettings) =>
        new DbOnTheRocks(_basePath, rocksDbSettings, _dbConfig, _logManager, sharedCache: _sharedCache);

    public IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings) where T : struct, Enum =>
        new ColumnsDb<T>(_basePath, rocksDbSettings, _dbConfig, _logManager, Array.Empty<T>(), sharedCache: _sharedCache);

    public string GetFullDbPath(RocksDbSettings rocksDbSettings) => DbOnTheRocks.GetFullDbPath(rocksDbSettings.DbPath, _basePath);
}
