// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using LightningDB;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Db.Lmdb;

/// <summary>
/// Hybrid <see cref="IColumnsDb{FlatDbColumns}"/> that routes the <see cref="FlatDbColumns.Account"/> and
/// <see cref="FlatDbColumns.Storage"/> columns to LMDB and delegates every other column (Metadata, the trie-node
/// columns) to the underlying RocksDB-backed columns DB unchanged - see PLAN-LMDB.md "Scope".
/// </summary>
public sealed class LmdbColumnsDbAdapter(IColumnsDb<FlatDbColumns> inner, LmdbDb account, LmdbDb storage, ILogManager logManager) : IColumnsDb<FlatDbColumns>
{
    // Exposed so the nested write-batch class below can reach them - a primary constructor parameter is only
    // captured for use within this class's own member bodies, not visible by name to a nested type. Internal
    // (not private) also lets tests verify per-column config (e.g. MaxOperationsPerWaveAccount/Storage) actually
    // reaches the right underlying LmdbDb instance end-to-end through the decorator.
    internal LmdbDb Account => account;
    internal LmdbDb Storage => storage;

    /// <summary>
    /// Periodically logs Account/Storage key counts, on-disk size, and call-volume/latency diagnostics - see
    /// PLAN-LMDB.md "Validation methodology". Unlike BTDB, LMDB has no separate resident-index allocator to query
    /// (the OS page cache decides what stays resident, not the library), so there is no "native RAM bytes" figure
    /// to log alongside these - see PLAN-LMDB.md's explanation of why that comparison doesn't carry over.
    /// </summary>
    /// <remarks>
    /// The latency/volume half of this line exists specifically to turn "this feels slow" into actual numbers
    /// without needing a profiler attached to a live sync - see <see cref="LmdbDb.GetDiagnostics"/>. Watch, in
    /// particular, whether <c>CreateSnapshot</c>'s call count tracks one-per-sync-request during snap sync (it
    /// bypasses <c>CachedReaderPersistence</c>'s cache via <c>ReaderFlags.Sync</c>) and whether its average
    /// latency is disproportionate to <c>Get</c>'s - that would point at the pool-of-readers cost genuinely
    /// dominating, as opposed to something upstream of this plugin entirely.
    /// </remarks>
    private readonly Timer _keyCountLogTimer = new(
        _ => LogKeyCounts(account, storage, logManager.GetClassLogger<LmdbColumnsDbAdapter>()),
        null,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30));

    private static void LogKeyCounts(LmdbDb account, LmdbDb storage, ILogger logger)
    {
        if (!logger.IsInfo) return;

        long accountCount = account.EstimatedCount;
        long storageCount = storage.EstimatedCount;
        long accountSize = account.GatherMetric().Size;
        long storageSize = storage.GatherMetric().Size;

        logger.Info(
            $"Lmdb key counts - Account: {accountCount:N0} keys ({FormatBytes(accountSize)} on disk), " +
            $"Storage: {storageCount:N0} keys ({FormatBytes(storageSize)} on disk)");
        logger.Info($"Lmdb diagnostics (last ~30s) - Account: {FormatDiagnostics(account.TakeAndResetDiagnostics())}, Storage: {FormatDiagnostics(storage.TakeAndResetDiagnostics())}");
        logger.Info($"Lmdb page stats (mdb_stat) - Account: {FormatStats(account.GetPerPartitionStats())}, Storage: {FormatStats(storage.GetPerPartitionStats())}");
    }

    private static string FormatDiagnostics(LmdbDb.DiagnosticsSnapshot d) =>
        $"Get x{d.GetCount:N0} (avg {d.AvgGetMicros:F1}us), " +
        $"CreateSnapshot x{d.CreateSnapshotCount:N0} (avg {d.AvgCreateSnapshotMicros:F1}us), " +
        $"WriteWave x{d.WriteWaveCount:N0} (avg {d.AvgWriteWaveMicros:F1}us = " +
        $"begin {d.AvgWriteBeginMicros:F1}us + apply {d.AvgWriteApplyMicros:F1}us + commit {d.AvgWriteCommitMicros:F1}us " +
        $"[lock-wait {d.AvgWriteCommitLockWaitMicros:F1}us + call {d.AvgWriteCommitCallMicros:F1}us], " +
        $"avg {d.AvgWriteWaveOps:F0} ops/wave), " +
        $"MapGrowths {d.MapGrowthCount:N0}, " +
        $"AlivePooledReaders {d.AliveSnapshotPoolReaders:N0}, " +
        $"AppendData {d.AppendedPutCount:N0}/{d.TotalPutCount:N0} puts " +
        $"(apply: appended {d.AvgAppendedApplyMicrosPerOp:F3}us/op x{d.AppendedApplyOpsCount:N0}, " +
        $"non-appended {d.AvgNonAppendedApplyMicrosPerOp:F3}us/op x{d.NonAppendedApplyOpsCount:N0})";

    /// <summary>See <see cref="LmdbDb.GetPerPartitionStats"/> - logged specifically to observe real B+tree page
    /// structure during a live sync and inform whether a non-default <see cref="ILmdbConfig.PageSizeBytes"/>
    /// would help (e.g. a low entries-per-leaf-page figure relative to how many entries should fit in a page of
    /// this size would suggest leaf pages are splitting before they're actually full). Uses the per-partition
    /// <c>mdb_stat</c> rather than the environment-wide <c>mdb_env_stat</c> (<see cref="LmdbDb.GetEnvironmentStats"/>)
    /// specifically so this stays correct for Account when <see cref="ILmdbConfig.EnableAccountPartitioning"/> is
    /// on - <c>mdb_env_stat</c> would otherwise report Account's tiny partition-name catalog instead of the real
    /// per-partition data (see <see cref="LmdbDb.GetEnvironmentStats"/>'s remarks); the two are identical in the
    /// common single, unnamed-database case.</summary>
    private static string FormatStats(global::LightningDB.Stats s)
    {
        long dataPages = s.BranchPages + s.LeafPages + s.OverflowPages;
        double avgEntriesPerLeafPage = s.LeafPages == 0 ? 0 : (double)s.Entries / s.LeafPages;
        return $"PageSize {s.PageSize}B, BTreeDepth {s.BTreeDepth}, " +
            $"BranchPages {s.BranchPages:N0}, LeafPages {s.LeafPages:N0}, OverflowPages {s.OverflowPages:N0}, " +
            $"Entries {s.Entries:N0}, avg {avgEntriesPerLeafPage:F1} entries/leaf-page, " +
            $"~{FormatBytes(dataPages * s.PageSize)} from page count";
    }

    private static string FormatBytes(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";

    public IDb GetColumnDb(FlatDbColumns key) => key switch
    {
        FlatDbColumns.Account => account,
        FlatDbColumns.Storage => storage,
        _ => inner.GetColumnDb(key),
    };

    public IEnumerable<FlatDbColumns> ColumnKeys => inner.ColumnKeys;

    public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() => new LmdbColumnsWriteBatch(this, inner.StartWriteBatch());

    /// <summary>
    /// <c>RocksDbPersistence.CreateReader</c>/<c>CreateWriteBatch</c> call this on every read/write-batch open,
    /// including at node startup via <c>FlatStateBoundary</c> - this is on the live path, not an edge case, so it
    /// must return a real point-in-time view.
    /// </summary>
    public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() =>
        new LmdbColumnDbSnapshot(inner.CreateSnapshot(), account.CreateSnapshot(), storage.CreateSnapshot());

    public IDbMeta.DbMetric GatherMetric()
    {
        IDbMeta.DbMetric innerMetric = inner.GatherMetric();
        IDbMeta.DbMetric accountMetric = account.GatherMetric();
        IDbMeta.DbMetric storageMetric = storage.GatherMetric();

        // Combined across every column, same convention as ColumnsDb<T>.GatherMetric() (see DbTracker's own
        // "metric for a columns db is combined across column" comment in DbMonitoringModule).
        return new IDbMeta.DbMetric
        {
            Size = innerMetric.Size + accountMetric.Size + storageMetric.Size,
            CacheSize = innerMetric.CacheSize,
            IndexSize = innerMetric.IndexSize,
            MemtableSize = innerMetric.MemtableSize,
            TotalReads = innerMetric.TotalReads + accountMetric.TotalReads + storageMetric.TotalReads,
            TotalWrites = innerMetric.TotalWrites + accountMetric.TotalWrites + storageMetric.TotalWrites,
        };
    }

    public void Flush(bool onlyWal = false)
    {
        inner.Flush(onlyWal);
        account.Flush(onlyWal);
        storage.Flush(onlyWal);
    }

    public void Dispose()
    {
        _keyCountLogTimer.Dispose();
        inner.Dispose();
        account.Dispose();
        storage.Dispose();
    }

    private sealed class LmdbColumnsWriteBatch(LmdbColumnsDbAdapter parent, IColumnsWriteBatch<FlatDbColumns> innerBatch) : IColumnsWriteBatch<FlatDbColumns>
    {
        private IWriteBatch? _accountBatch;
        private IWriteBatch? _storageBatch;

        public IWriteBatch GetColumnBatch(FlatDbColumns key) => key switch
        {
            FlatDbColumns.Account => _accountBatch ??= parent.Account.StartWriteBatch(),
            FlatDbColumns.Storage => _storageBatch ??= parent.Storage.StartWriteBatch(),
            _ => innerBatch.GetColumnBatch(key),
        };

        public void Clear()
        {
            innerBatch.Clear();
            _accountBatch?.Clear();
            _storageBatch?.Clear();
        }

        // Commits the RocksDB batch and the LMDB batch(es) that were touched. These are two independent storage
        // engines, so this cannot be a single atomic commit across both - acceptable for this experiment (see
        // PLAN-LMDB.md), but worth calling out for anyone relying on cross-column atomicity here.
        public void Dispose()
        {
            _accountBatch?.Dispose();
            _storageBatch?.Dispose();
            innerBatch.Dispose();
        }
    }

    /// <summary>
    /// The three storage engines behind one <see cref="LmdbColumnsDbAdapter"/> snapshot at once, since a caller
    /// (<c>RocksDbPersistence</c>) disposes the whole thing as a unit. Not a cross-engine atomic snapshot - the
    /// RocksDB snapshot and the two LMDB read-transaction pools are each internally consistent, but taken via
    /// three separate calls, so a write landing between them could be visible in one and not the other. The same
    /// caveat already applies to <see cref="LmdbColumnsWriteBatch"/>'s non-atomic commit across engines.
    /// </summary>
    private sealed class LmdbColumnDbSnapshot(
        IColumnDbSnapshot<FlatDbColumns> inner,
        IKeyValueStoreSnapshot account,
        IKeyValueStoreSnapshot storage) : IColumnDbSnapshot<FlatDbColumns>
    {
        public IReadOnlyKeyValueStore GetColumn(FlatDbColumns key) => key switch
        {
            FlatDbColumns.Account => account,
            FlatDbColumns.Storage => storage,
            _ => inner.GetColumn(key),
        };

        public void Dispose()
        {
            inner.Dispose();
            account.Dispose();
            storage.Dispose();
        }
    }
}
