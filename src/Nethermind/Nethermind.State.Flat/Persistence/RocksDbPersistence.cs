// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence;

public class RocksDbPersistence(IColumnsDb<FlatDbColumns> db, ILogManager logManager, IFlatDbConfig? config = null) : IPersistence
{
    // TEST-ONLY (benchmark branch): synchronously rewrite the Storage column SSTs with the current
    // column options before any benchmarking, so option changes (compression/block size) apply to
    // the premade fixture data. Gated by env var; blocks startup for the duration of the compaction.
    private readonly bool _storageCompacted = MaybeCompactStorageForTest(db, logManager);

    private readonly WriteBufferAdjuster _adjuster = new(db, config?.PersistenceWriteBufferFloor ?? WriteBufferAdjuster.DefaultWriteBufferFloor);
    private int _layoutPersisted = BasePersistence.ValidateLayoutReturnFlag(db, FlatLayout.Flat);
    private readonly bool _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<RocksDbPersistence>());

    private static bool MaybeCompactStorageForTest(IColumnsDb<FlatDbColumns> db, ILogManager logManager)
    {
        if (Environment.GetEnvironmentVariable("NETHERMIND_TEST_COMPACT_FLAT_STORAGE") != "1") return false;

        ILogger logger = logManager.GetClassLogger<RocksDbPersistence>();
        long start = Stopwatch.GetTimestamp();
        if (logger.IsWarn) logger.Warn("TEST: compacting flat Storage column (blocking startup)...");
        db.GetColumnDb(FlatDbColumns.Storage).Compact();
        if (logger.IsWarn) logger.Warn($"TEST: flat Storage column compaction completed in {Stopwatch.GetElapsedTime(start).TotalSeconds:F0}s");
        return true;
    }

    public void Flush() => db.Flush();

    public void Clear() => BasePersistence.ClearAllColumns(db);

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        IColumnDbSnapshot<FlatDbColumns> snapshot = db.CreateSnapshot();
        try
        {
            BaseTriePersistence.Reader trieReader = new(
                snapshot.GetColumn(FlatDbColumns.StateTopNodes),
                snapshot.GetColumn(FlatDbColumns.StateNodes),
                snapshot.GetColumn(FlatDbColumns.StorageNodes),
                snapshot.GetColumn(FlatDbColumns.FallbackNodes)
            );

            StateId currentState = BasePersistence.ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

            return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>, BaseTriePersistence.Reader>(
                new BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>(
                    new BaseFlatPersistence.Reader(
                        (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Account),
                        (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Storage),
                        isPreimageMode: false,
                        rlpWrapSlots: _rlpWrapSlots
                    )
                ),
                trieReader,
                currentState,
                new Reactive.AnonymousDisposable(() =>
                {
                    snapshot.Dispose();
                })
            );
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        IColumnDbSnapshot<FlatDbColumns> dbSnap = db.CreateSnapshot();
        StateId currentState = BasePersistence.ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (from != StateId.Sync && to != StateId.Sync && currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();

        IWriteBatch accountBatch = _adjuster.Wrap(batch, FlatDbColumns.Account, flags);
        IWriteBatch storageBatch = _adjuster.Wrap(batch, FlatDbColumns.Storage, flags);
        IWriteBatch stateTopNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateTopNodes, flags);
        IWriteBatch stateNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateNodes, flags);
        IWriteBatch storageNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StorageNodes, flags);
        IWriteBatch fallbackNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.FallbackNodes, flags);

        BaseTriePersistence.WriteBatch trieWriteBatch = new(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            stateTopNodesBatch,
            stateNodesBatch,
            storageNodesBatch,
            fallbackNodesBatch,
            flags);

        StateId fromCopy = from;
        StateId toCopy = to;

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(
            new BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>(
                new BaseFlatPersistence.WriteBatch(
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Account),
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
                    accountBatch,
                    storageBatch,
                    flags,
                    rlpWrapSlots: _rlpWrapSlots
                )
            ),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                if (fromCopy != StateId.Sync && toCopy != StateId.Sync)
                    BasePersistence.SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), toCopy);
                if (_rlpWrapSlots)
                    BasePersistence.RecordLayoutOnFirstBatch(batch.GetColumnBatch(FlatDbColumns.Metadata), ref _layoutPersisted, FlatLayout.Flat);
                batch.Dispose();
                dbSnap.Dispose();
                _adjuster.OnBatchDisposed();
                if (!flags.HasFlag(WriteFlags.DisableWAL))
                {
                    db.Flush(onlyWal: true);
                }
            })
        );
    }

}
