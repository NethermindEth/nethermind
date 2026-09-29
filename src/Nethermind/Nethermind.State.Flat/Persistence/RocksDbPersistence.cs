// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.TrieNodeLog;

namespace Nethermind.State.Flat.Persistence;

public class RocksDbPersistence(IColumnsDb<FlatDbColumns> db, ILogManager logManager, ITrieNodeLog trieNodeLog, IFlatDbConfig? config = null) : IPersistence
{
    private readonly WriteBufferAdjuster _adjuster = new(db, config?.PersistenceWriteBufferFloor ?? WriteBufferAdjuster.DefaultWriteBufferFloor);
    private int _layoutPersisted = BasePersistence.ValidateLayoutReturnFlag(db, FlatLayout.Flat);
    private readonly bool _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<RocksDbPersistence>());

    public void Flush()
    {
        trieNodeLog.Drain();
        db.Flush();
    }

    public void Clear()
    {
        trieNodeLog.Clear();
        BasePersistence.ClearAllColumns(db);
    }

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        // Pinned before the snapshot so a generation merged and deleted in between stays readable, bound after it
        // so the view serves exactly the log version the snapshot's metadata confirms.
        ITrieNodeLog.IView logView = trieNodeLog.PinLiveGenerations();
        IColumnDbSnapshot<FlatDbColumns> snapshot = db.CreateSnapshot(flags);
        try
        {
            logView.Bind(snapshot.GetColumn(FlatDbColumns.Metadata));
            BaseTriePersistence.Reader trieReader = new(
                logView.Wrap(FlatDbColumns.StateTopNodes, snapshot.GetColumn(FlatDbColumns.StateTopNodes)),
                logView.Wrap(FlatDbColumns.StateNodes, snapshot.GetColumn(FlatDbColumns.StateNodes)),
                logView.Wrap(FlatDbColumns.StorageNodes, snapshot.GetColumn(FlatDbColumns.StorageNodes)),
                logView.Wrap(FlatDbColumns.FallbackNodes, snapshot.GetColumn(FlatDbColumns.FallbackNodes))
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
                    logView.Dispose();
                })
            );
        }
        catch
        {
            snapshot.Dispose();
            logView.Dispose();
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

        // Sync and import batches scan the trie columns for range deletes, so they go straight to RocksDB.
        ITrieNodeLog.IWriteBatch logBatch;
        try
        {
            logBatch = trieNodeLog.StartWriteBatch(bypass: from == StateId.Sync || to == StateId.Sync || flags.HasFlag(WriteFlags.DisableWAL));
        }
        catch
        {
            dbSnap.Dispose();
            throw;
        }

        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();

        IWriteBatch accountBatch = _adjuster.Wrap(batch, FlatDbColumns.Account, flags);
        IWriteBatch storageBatch = _adjuster.Wrap(batch, FlatDbColumns.Storage, flags);
        IWriteBatch stateTopNodesBatch = logBatch.Wrap(FlatDbColumns.StateTopNodes, _adjuster.Wrap(batch, FlatDbColumns.StateTopNodes, flags));
        IWriteBatch stateNodesBatch = logBatch.Wrap(FlatDbColumns.StateNodes, _adjuster.Wrap(batch, FlatDbColumns.StateNodes, flags));
        IWriteBatch storageNodesBatch = logBatch.Wrap(FlatDbColumns.StorageNodes, _adjuster.Wrap(batch, FlatDbColumns.StorageNodes, flags));
        IWriteBatch fallbackNodesBatch = logBatch.Wrap(FlatDbColumns.FallbackNodes, _adjuster.Wrap(batch, FlatDbColumns.FallbackNodes, flags));

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
                // The log is made durable and its version put into this batch's metadata before RocksDB commits,
                // and the log only seals generations once RocksDB has.
                logBatch.Commit(batch.GetColumnBatch(FlatDbColumns.Metadata));
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
                logBatch.Dispose();
            })
        );
    }

}
