// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
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
    // captured for use within this class's own member bodies, not visible by name to a nested type.
    private LmdbDb Account => account;
    private LmdbDb Storage => storage;

    /// <summary>
    /// Periodically logs Account/Storage key counts and on-disk size - see PLAN-LMDB.md "Validation methodology".
    /// Unlike BTDB, LMDB has no separate resident-index allocator to query (the OS page cache decides what stays
    /// resident, not the library), so there is no "native RAM bytes" figure to log alongside these - see
    /// PLAN-LMDB.md's explanation of why that comparison doesn't carry over. Read-only and O(1) per tick (LMDB's
    /// own <c>mdb_stat</c> entry count) - no interaction with the write path this logs alongside.
    /// </summary>
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
