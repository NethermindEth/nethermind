// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
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
    /// <summary>
    /// Raised once (a static constructor runs at most once per process, the first time this decorator type is
    /// used - i.e. only when the LMDB plugin is actually enabled) since <see cref="ThreadPool.SetMinThreads"/>
    /// is a genuinely process-wide .NET runtime setting, not something tied to any one instance.
    /// </summary>
    private const int MinThreadPoolThreads = 512;

    /// <summary>
    /// Works around .NET ThreadPool starvation caused by this plugin's own write-coordination design - see
    /// PLAN-LMDB.md "Known issues". <see cref="LmdbDb.SubmitWrite"/> blocks synchronously
    /// (<c>ManualResetEventSlim.Wait()</c>, deliberately - see that method's own remarks on why not a
    /// <c>TaskCompletionSource</c>) for every caller that doesn't win the group-commit leader race. If snap
    /// sync's dispatcher fans out many concurrent write-submitting tasks at once, most of them occupy a real
    /// ThreadPool thread while doing nothing (no CPU, no I/O) - and the ThreadPool only grows by roughly one
    /// thread per 500ms-1s under sustained demand by design (to avoid oversubscription), which can turn a burst
    /// of concurrent writers into a multi-second stall for everyone, regardless of how little work any
    /// individual one has to do. Observed in practice: writes with wildly different operation counts (2 through
    /// 50,000+) reporting near-identical multi-second durations, with ~0% disk activity and negligible GC pause
    /// during the stall - not explainable by real storage-engine or GC cost, but exactly the signature of
    /// ThreadPool starvation from synchronous blocking. Only raises the minimum if it is currently lower than
    /// <see cref="MinThreadPoolThreads"/> - never lowers whatever Nethermind's own startup configuration already
    /// set.
    /// </summary>
    static LmdbDbFactoryDecorator()
    {
        ThreadPool.GetMinThreads(out int currentWorkerMin, out int currentIoMin);
        ThreadPool.SetMinThreads(Math.Max(currentWorkerMin, MinThreadPoolThreads), Math.Max(currentIoMin, MinThreadPoolThreads));
    }

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
