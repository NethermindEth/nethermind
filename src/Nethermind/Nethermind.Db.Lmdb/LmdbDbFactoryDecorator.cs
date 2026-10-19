// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Db.Lmdb;

/// <summary>
/// Decorates the registered <see cref="IDbFactory"/> so that the FlatDb Account/Storage columns are backed by
/// LMDB instead of RocksDB, while every other database (including the rest of the FlatDb columns) is created
/// through the decorated factory unchanged - see PLAN-LMDB.md "Architecture".
/// </summary>
public sealed class LmdbDbFactoryDecorator(IDbFactory innerFactory, ILmdbConfig config, ISyncConfig syncConfig, ILogManager logManager) : IDbFactory
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
    /// <c>TaskCompletionSource</c>) for every caller, waiting on the single dedicated writer thread to commit its
    /// wave. If snap
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
        //ThreadPool.GetMinThreads(out int currentWorkerMin, out int currentIoMin);
        //ThreadPool.SetMinThreads(Math.Max(currentWorkerMin, MinThreadPoolThreads), Math.Max(currentIoMin, MinThreadPoolThreads));
    }

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HeartbeatStallThreshold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatAliveLogInterval = TimeSpan.FromMinutes(1);
    private static int _gcPauseHeartbeatStarted;

    /// <summary>
    /// Independent, GC-attributable confirmation for the multi-second-to-minute total process freezes observed
    /// during snap sync phase transitions - see PLAN-LMDB.md "Known issues". <see cref="LmdbDb.SubmitWrite"/>
    /// and <see cref="LmdbDb.CreateSnapshot"/> only measure GC pause *during calls that are actually made* -
    /// during a genuine whole-process freeze nothing is being submitted to LMDB at all, so that measurement sees
    /// nothing. This dedicated background thread instead pings a monotonic timestamp on a fixed interval,
    /// independently of whether any LmdbDb call is in flight (a blocking GC suspends it exactly like every other
    /// managed thread, so a late wake-up here is itself evidence of a process-wide pause), and logs
    /// <see cref="GC.GetTotalPauseDuration"/>'s delta whenever a gap exceeds <see cref="HeartbeatStallThreshold"/>.
    /// If that delta accounts for (most of) the gap, the freeze is attributable to GC - most likely
    /// <c>Synchronizer.GCOnFeedFinished</c>'s blocking, compacting <c>GC.Collect(2, ...)</c> fired inline from
    /// <c>MultiSyncModeSelector.Update()</c> on a sync mode change - rather than something else entirely (OS-level
    /// stall, driver, etc.). Started once per process, the first time the LMDB plugin is actually enabled.
    /// </summary>
    /// <remarks>
    /// Also logs an unconditional "alive" line once per <see cref="HeartbeatAliveLogInterval"/> (and once
    /// immediately on startup), independent of whether any stall was ever detected. Without this, silence in the
    /// log is ambiguous - it could mean "no stall occurred" or "this thread isn't running / its output isn't
    /// reaching the log for some unrelated reason (stale deploy, logger filtering, etc.)". The periodic line turns
    /// that ambiguity into a direct, positive confirmation that the watchdog itself is alive and logging.
    /// </remarks>
    private readonly Thread? _gcPauseHeartbeatThread = StartGcPauseHeartbeatOnce(logManager);

    private static Thread? StartGcPauseHeartbeatOnce(ILogManager logManager)
    {
        if (Interlocked.CompareExchange(ref _gcPauseHeartbeatStarted, 1, 0) != 0) return null;

        ILogger logger = logManager.GetClassLogger<LmdbDbFactoryDecorator>();
        Thread thread = new(() => GcPauseHeartbeatLoop(logger))
        {
            IsBackground = true,
            Name = "Lmdb.GcPauseHeartbeat",
        };
        thread.Start();
        return thread;
    }

    private static void GcPauseHeartbeatLoop(ILogger logger)
    {
        if (logger.IsInfo) logger.Info("Lmdb heartbeat started - will log every " +
            $"{HeartbeatAliveLogInterval.TotalMinutes:F0}m while alive, and immediately on any gap over " +
            $"{HeartbeatStallThreshold.TotalSeconds:F0}s.");

        long lastTimestamp = Stopwatch.GetTimestamp();
        long lastAliveLogTimestamp = lastTimestamp;
        TimeSpan lastTotalPause = GC.GetTotalPauseDuration();

        while (true)
        {
            Thread.Sleep(HeartbeatInterval);

            // Piggybacks on this already-running tick rather than starting a second thread - see
            // WindowsProcessDiagnostics' remarks for why the resulting up-to-~250ms staleness is an acceptable
            // tradeoff for a diagnostic that must not add real syscall cost to every single LMDB write.
            WindowsProcessDiagnostics.UpdateCachedPageFaultCount();

            long now = Stopwatch.GetTimestamp();
            TimeSpan gap = Stopwatch.GetElapsedTime(lastTimestamp, now);
            TimeSpan totalPause = GC.GetTotalPauseDuration();

            if (gap > HeartbeatStallThreshold && logger.IsWarn)
            {
                TimeSpan pauseDuringGap = totalPause - lastTotalPause;
                logger.Warn(
                    $"Lmdb heartbeat detected a {gap.TotalSeconds:F1}s gap since the last tick " +
                    $"(expected ~{HeartbeatInterval.TotalMilliseconds:F0}ms) - GC pause during gap: " +
                    $"{pauseDuringGap.TotalSeconds:F1}s, Gen0/1/2 collection counts: " +
                    $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
            }

            if (Stopwatch.GetElapsedTime(lastAliveLogTimestamp, now) >= HeartbeatAliveLogInterval && logger.IsInfo)
            {
                logger.Info(
                    $"Lmdb heartbeat alive - no gap over {HeartbeatStallThreshold.TotalSeconds:F0}s in the last " +
                    $"~{HeartbeatAliveLogInterval.TotalMinutes:F0}m, total GC pause so far: " +
                    $"{totalPause.TotalSeconds:F1}s, Gen0/1/2 collection counts: " +
                    $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                lastAliveLogTimestamp = now;
            }

            lastTimestamp = now;
            lastTotalPause = totalPause;
        }
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
        int accountMaxOperationsPerWave = config.MaxOperationsPerWaveAccount > 0 ? config.MaxOperationsPerWaveAccount : config.MaxOperationsPerWave;
        int storageMaxOperationsPerWave = config.MaxOperationsPerWaveStorage > 0 ? config.MaxOperationsPerWaveStorage : config.MaxOperationsPerWave;
        int accountPageSizeBytes = config.PageSizeBytesAccount > 0 ? config.PageSizeBytesAccount : config.PageSizeBytes;
        int storagePageSizeBytes = config.PageSizeBytesStorage > 0 ? config.PageSizeBytesStorage : config.PageSizeBytes;

        // Storage partitioning (EnableStoragePartitioning) and Storage multi-environment (EnableStorageMultiEnvironment)
        // were both implemented, deployed live, and reverted - see ILmdbConfig's remarks and PLAN-LMDB.md
        // "Storage partitioning" / "Separate LMDB environments per partition" for why. Storage always uses a
        // single plain LmdbDb now.
        int accountPartitionCount = config.EnableAccountPartitioning ? Math.Max(1, syncConfig.SnapSyncAccountRangePartitionCount) : 1;
        LmdbDb account = new("LmdbAccount", Path.Combine(basePath, "account.lmdb"), config.InitialMapSizeBytes, config.MaxReaders, accountMaxOperationsPerWave, accountPartitionCount, logManager, accountPageSizeBytes);
        LmdbDb storage = new("LmdbStorage", Path.Combine(basePath, "storage.lmdb"), config.InitialMapSizeBytes, config.MaxReaders, storageMaxOperationsPerWave, partitionCount: 1, logManager, storagePageSizeBytes);

        // T has just been confirmed to be FlatDbColumns above, so these casts merely tell the compiler what the
        // runtime already knows; IColumnsDb<T> cannot be constructed generically from a non-generic adapter type.
        IColumnsDb<FlatDbColumns> adapter = new LmdbColumnsDbAdapter((IColumnsDb<FlatDbColumns>)(object)rocksColumnsDb, account, storage, logManager);
        return (IColumnsDb<T>)(object)adapter;
    }

    public string GetFullDbPath(DbSettings dbSettings) => innerFactory.GetFullDbPath(dbSettings);
}
