// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.TrieNodeLog;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Persistence implementation that stores flat state data in the trie node columns (StateNodes/StorageNodes)
/// instead of separate Account/Storage columns.
/// </summary>
public class FlatInTriePersistence(IColumnsDb<FlatDbColumns> db, ILogManager logManager, ITrieNodeLog trieNodeLog) : IPersistence
{
    private readonly WriteBufferAdjuster _adjuster = new(db);
    private int _layoutPersisted = BasePersistence.ValidateLayoutReturnFlag(db, FlatLayout.FlatInTrie);
    private readonly bool _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.StorageNodes), logManager.GetClassLogger<FlatInTriePersistence>());
    private readonly PrefetchSampling _prefetchSampling = new();

    public void Flush()
    {
        trieNodeLog.Drain();
        db.Flush();
    }

    public void Clear()
    {
        // The wipe is on record before the log drops generations the database still counts on.
        BasePersistence.MarkWipeStarted(db);
        trieNodeLog.Clear();
        BasePersistence.ClearAllColumns(db);
    }

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        // Only trie nodes go through the log; the flat entries sharing these columns are read and scanned in RocksDB.
        ITrieNodeLog.IView view = trieNodeLog.OpenView(db, flags);
        try
        {
            BaseTriePersistence.Reader trieReader = new(
                view.GetColumn(FlatDbColumns.StateTopNodes),
                view.GetColumn(FlatDbColumns.StateNodes),
                view.GetColumn(FlatDbColumns.StorageNodes),
                view.GetColumn(FlatDbColumns.FallbackNodes)
            );

            StateId currentState = BasePersistence.ReadCurrentState(view.Snapshot.GetColumn(FlatDbColumns.Metadata));

            return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>, BaseTriePersistence.Reader>(
                new BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>(
                    new BaseFlatPersistence.Reader(
                        (ISortedKeyValueStore)view.Snapshot.GetColumn(FlatDbColumns.StateNodes),
                        (ISortedKeyValueStore)view.Snapshot.GetColumn(FlatDbColumns.StorageNodes),
                        isPreimageMode: false,
                        rlpWrapSlots: _rlpWrapSlots
                    ),
                    sampling: _prefetchSampling
                ),
                trieReader,
                currentState,
                view
            );
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags) => CreateWriteBatch(from, to, flags, atomic: false);

    private void MarkLayoutCommitted() => Volatile.Write(ref _layoutPersisted, 1);

    /// <inheritdoc/>
    public IPersistence.IAtomicPersistenceBatch CreateAtomicWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) =>
        (IPersistence.IAtomicPersistenceBatch)CreateWriteBatch(from, to, flags, atomic: true);

    private IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags, bool atomic)
    {
        // Sync and import batches scan the trie columns for range deletes, so the log is merged into RocksDB before
        // the snapshot they scan is taken, and they bypass it.
        bool bypass = atomic || from == StateId.Sync || to == StateId.Sync || flags.HasFlag(WriteFlags.DisableWAL);
        if (bypass) trieNodeLog.Drain();

        IColumnDbSnapshot<FlatDbColumns> dbSnap = BasePersistence.CreateWriteSnapshot(db, from, to);
        IColumnsWriteBatch<FlatDbColumns> batch;
        try
        {
            batch = atomic ? db.StartAtomicWriteBatch() : db.StartWriteBatch();
        }
        catch
        {
            dbSnap.Dispose();
            throw;
        }

        ITrieNodeLog.IWriteBatch logBatch;
        try
        {
            logBatch = atomic ? NullTrieNodeLog.Instance : trieNodeLog.StartWriteBatch(batch, bypass);
        }
        catch
        {
            batch.Clear();
            batch.Dispose();
            dbSnap.Dispose();
            throw;
        }

        try
        {
            IWriteBatch stateTopNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateTopNodes, flags);
            IWriteBatch stateNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateNodes, flags);
            IWriteBatch storageNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StorageNodes, flags);
            IWriteBatch fallbackNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.FallbackNodes, flags);

            // The log wraps the trie node writes only; the flat entries written below into the same columns must stay in
            // RocksDB, where the flat reader iterates them.
            BaseTriePersistence.WriteBatch trieWriteBatch = new(
                (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
                (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
                (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
                (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
                logBatch.Wrap(FlatDbColumns.StateTopNodes, stateTopNodesBatch),
                logBatch.Wrap(FlatDbColumns.StateNodes, stateNodesBatch),
                logBatch.Wrap(FlatDbColumns.StorageNodes, storageNodesBatch),
                logBatch.Wrap(FlatDbColumns.FallbackNodes, fallbackNodesBatch),
                flags);

            StateId fromCopy = from;
            StateId toCopy = to;
            BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch> flatWriteBatch = new(
                new BaseFlatPersistence.WriteBatch(
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
                    stateNodesBatch,
                    storageNodesBatch,
                    flags,
                    rlpWrapSlots: _rlpWrapSlots
                )
            );
            IWriteOnlyKeyValueStore metadata = batch.GetColumnBatch(FlatDbColumns.Metadata);
            if (atomic)
            {
                AtomicPersistenceBatch transaction = new((IAtomicColumnsWriteBatch<FlatDbColumns>)batch, dbSnap, from, to, FlatLayout.FlatInTrie, _rlpWrapSlots && Volatile.Read(ref _layoutPersisted) == 0, MarkLayoutCommitted, db, flags, _adjuster, logManager.GetClassLogger<FlatInTriePersistence>());
                return new BasePersistence.AtomicWriteBatch<BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(flatWriteBatch, trieWriteBatch, metadata, transaction);
            }

            return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(
                flatWriteBatch,
                trieWriteBatch,
                metadata,
                new Reactive.AnonymousDisposable(() =>
                {
                    // The log is made durable and its version put into this batch's metadata before RocksDB commits, and
                    // confirmed to the log only once RocksDB has committed and flushed its WAL.
                    bool batchWritten = false;
                    try
                    {
                        logBatch.Commit();
                        if (fromCopy != StateId.Sync && toCopy != StateId.Sync)
                            BasePersistence.SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), toCopy);
                        if (_rlpWrapSlots)
                            BasePersistence.RecordLayoutOnFirstBatch(batch.GetColumnBatch(FlatDbColumns.Metadata), ref _layoutPersisted, FlatLayout.FlatInTrie);
                        batchWritten = true;
                        batch.Dispose();
                        if (!flags.HasFlag(WriteFlags.DisableWAL))
                        {
                            // A log-backed batch needs the sync to throw, so a version RocksDB did not make durable is never confirmed;
                            // without the log the WAL flush keeps its previous error handling.
                            if (bypass || trieNodeLog is NullTrieNodeLog) db.Flush(onlyWal: true);
                            else db.SyncWal();
                        }
                        logBatch.Confirm();
                    }
                    finally
                    {
                        if (!batchWritten)
                        {
                            // The log side failed, so nothing of this batch may reach RocksDB.
                            batch.Clear();
                            batch.Dispose();
                        }
                        dbSnap.Dispose();
                        _adjuster.OnBatchDisposed();
                        logBatch.Dispose();
                    }
                })
            );
        }
        catch
        {
            try
            {
                batch.Clear();
                batch.Dispose();
            }
            finally
            {
                logBatch.Dispose();
                dbSnap.Dispose();
            }
            throw;
        }
    }
}
