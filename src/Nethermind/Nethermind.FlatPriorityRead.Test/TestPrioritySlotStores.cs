// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Testably.Abstractions;

namespace Nethermind.FlatPriorityRead.Test;

/// <summary>Builds each <see cref="FlatPriorityReadBackend"/> over a test directory, wired as in production.</summary>
public static class TestPrioritySlotStores
{
    public static IPrioritySlotStore Create(FlatPriorityReadBackend backend, string directory)
    {
        FlatPriorityReadConfig config = new() { DbPath = directory, LmdbMapSize = 256L * 1024 * 1024 };
        return backend switch
        {
            FlatPriorityReadBackend.Memory => new MemoryPrioritySlotStore(),
            FlatPriorityReadBackend.Lmdb => new LmdbPrioritySlotStore(config),
            FlatPriorityReadBackend.RocksDb => new RocksDbPrioritySlotStore(CreateRocksDbFactory(config, directory), config),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
        };
    }

    private static RocksDbFactory CreateRocksDbFactory(IFlatPriorityReadConfig config, string directory)
    {
        DbConfig dbConfig = new();
        RocksDbConfigFactory baseFactory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false);
        return new RocksDbFactory(
            new FlatPriorityReadRocksDbConfigFactory(baseFactory, config, dbConfig),
            dbConfig,
            new HyperClockCacheWrapper(),
            new RealFileSystem(),
            LimboLogs.Instance,
            directory);
    }
}
