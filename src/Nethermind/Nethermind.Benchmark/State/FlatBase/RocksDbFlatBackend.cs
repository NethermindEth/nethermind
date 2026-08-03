// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Benchmarks.State.FlatBase;

/// <summary>
/// The production-tuned RocksDB flat store: a <see cref="ColumnsDb{T}"/> over
/// <see cref="FlatDbColumns"/> opened with the default <see cref="DbConfig"/> Flat/FlatAccount/FlatStorage
/// option strings, plus dedicated HyperClockCaches for the Account (300 MiB) and Storage (700 MiB)
/// columns — the same 30/70 split of the 1 GiB flat block-cache budget that
/// <c>Nethermind.Init.Modules.FlatRocksDbConfigAdjuster</c> applies (that adjuster is internal, so its
/// two-column cache wiring is mirrored here). Reads go through a DB snapshot, like
/// <c>RocksDbPersistence.CreateReader</c>.
/// </summary>
internal sealed class RocksDbFlatBackend : IFlatPointReadBackend
{
    private const double AccountCacheShare = 0.3;
    private const double StorageCacheShare = 0.7;
    private const ulong BlockCacheBudget = 1024UL * 1024 * 1024;

    private readonly HyperClockCacheWrapper _accountCache;
    private readonly HyperClockCacheWrapper _storageCache;
    private readonly ColumnsDb<FlatDbColumns> _db;
    private readonly Lock _snapshotLock = new();
    private IColumnDbSnapshot<FlatDbColumns> _snapshot;
    private IReadOnlyKeyValueStore _accountColumn;
    private IReadOnlyKeyValueStore _storageColumn;

    public RocksDbFlatBackend(string basePath)
    {
        _accountCache = new HyperClockCacheWrapper((ulong)(BlockCacheBudget * AccountCacheShare));
        _storageCache = new HyperClockCacheWrapper((ulong)(BlockCacheBudget * StorageCacheShare));

        DbConfig dbConfig = new();
        RocksDbConfigFactory baseFactory = new(
            dbConfig, new PruningConfig(), new TestHardwareInfo(), NullLogManager.Instance);
        CacheInjectingConfigFactory factory = new(baseFactory, _accountCache, _storageCache);

        _db = new ColumnsDb<FlatDbColumns>(
            basePath, new DbSettings("Flat", "flat"), dbConfig, factory, NullLogManager.Instance,
            Enum.GetValues<FlatDbColumns>());
    }

    public void WriteShard(FlatDbColumns column, byte[][] keys, byte[][] values, int count)
    {
        using IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        IWriteBatch columnBatch = batch.GetColumnBatch(column);
        for (int i = 0; i < count; i++)
            columnBatch.PutSpan(keys[i], values[i], WriteFlags.DisableWAL);
    }

    /// <summary>Materialize the bulk load: flush memtables to SSTs and run a full compaction so
    /// benchmark reads see the steady-state LSM shape rather than a pile of L0 files.</summary>
    public void FinishWrites()
    {
        _db.Flush();
        _db.Compact();
        _db.Flush();
    }

    public IFlatReadSession BeginSession()
    {
        // Sessions are opened concurrently by the benchmark's reader threads; guard the one-time
        // snapshot creation (reads through the snapshot columns are thread-safe).
        lock (_snapshotLock)
        {
            if (_snapshot is null)
            {
                _snapshot = ((IColumnsDb<FlatDbColumns>)_db).CreateSnapshot();
                _accountColumn = _snapshot.GetColumn(FlatDbColumns.Account);
                _storageColumn = _snapshot.GetColumn(FlatDbColumns.Storage);
            }
        }

        return new Session(this);
    }

    public void Dispose()
    {
        _snapshot?.Dispose();
        _db.Dispose();
        _accountCache.Dispose();
        _storageCache.Dispose();
    }

    private sealed class Session(RocksDbFlatBackend backend) : IFlatReadSession
    {
        public int GetAccount(ReadOnlySpan<byte> key20, Span<byte> valueOut) =>
            backend._accountColumn.Get(key20, valueOut);

        public int GetSlot(ReadOnlySpan<byte> key52, Span<byte> valueOut) =>
            backend._storageColumn.Get(key52, valueOut);

        public void Dispose() { }
    }

    /// <summary>Mirror of the internal <c>FlatRocksDbConfigAdjuster</c>: hand the Account and Storage
    /// columns their dedicated HyperClockCaches, leaving every other option to the production
    /// <see cref="DbConfig"/> strings resolved by the wrapped factory.</summary>
    private sealed class CacheInjectingConfigFactory(
        IRocksDbConfigFactory inner,
        HyperClockCacheWrapper accountCache,
        HyperClockCacheWrapper storageCache) : IRocksDbConfigFactory
    {
        public IRocksDbConfig GetForDatabase(string databaseName, string columnName)
        {
            IRocksDbConfig config = inner.GetForDatabase(databaseName, columnName);
            IntPtr? cacheHandle = columnName switch
            {
                nameof(FlatDbColumns.Account) => accountCache.Handle,
                nameof(FlatDbColumns.Storage) => storageCache.Handle,
                _ => null,
            };

            return cacheHandle is null
                ? config
                : new AdjustedRocksdbConfig(config, "", config.WriteBufferSize.GetValueOrDefault(), cacheHandle);
        }
    }
}
