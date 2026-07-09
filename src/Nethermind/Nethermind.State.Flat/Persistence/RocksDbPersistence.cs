// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence;

public class RocksDbPersistence(IColumnsDb<FlatDbColumns> db, ILogManager logManager, IFlatDbConfig? config = null) : IPersistence
{
    private readonly WriteBufferAdjuster _adjuster = new(db, config?.PersistenceWriteBufferFloor ?? WriteBufferAdjuster.DefaultWriteBufferFloor);
    private int _layoutPersisted = BasePersistence.ValidateLayoutReturnFlag(db, FlatLayout.Flat);
    private readonly bool _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<RocksDbPersistence>());
    private readonly bool _useSstIngestion = config?.PersistViaSstIngestion ?? false;

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

    // Persist by building sorted SST file(s) per column (from a byte-capped in-memory buffer) and ingesting them
    // as a metadata-only add, instead of a single large WriteBatch. This bypasses the memtable, so persisting a
    // compacted snapshot no longer triggers the flush -> L0 pile-up -> compaction burst that saturates I/O and
    // stalls concurrent reads for seconds. Peak persist memory is higher than the streaming WriteBatch (the buffer
    // holds up to the byte cap per column on the managed heap), which is why it is capped and default-off.
    private IPersistence.IWriteBatch CreateIngestWriteBatch(IColumnDbSnapshot<FlatDbColumns> dbSnap, StateId to)
    {
        IWriteBatch Ingest(FlatDbColumns column) => ((ISstIngestible)db.GetColumnDb(column)).StartSstIngestBatch();

        IWriteBatch accountBatch = Ingest(FlatDbColumns.Account);
        IWriteBatch storageBatch = Ingest(FlatDbColumns.Storage);
        IWriteBatch stateTopNodesBatch = Ingest(FlatDbColumns.StateTopNodes);
        IWriteBatch stateNodesBatch = Ingest(FlatDbColumns.StateNodes);
        IWriteBatch storageNodesBatch = Ingest(FlatDbColumns.StorageNodes);
        IWriteBatch fallbackNodesBatch = Ingest(FlatDbColumns.FallbackNodes);

        BaseTriePersistence.WriteBatch trieWriteBatch = new(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            stateTopNodesBatch,
            stateNodesBatch,
            storageNodesBatch,
            fallbackNodesBatch,
            WriteFlags.None);

        StateId toCopy = to;

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(
            new BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>(
                new BaseFlatPersistence.WriteBatch(
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Account),
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
                    accountBatch,
                    storageBatch,
                    WriteFlags.None,
                    rlpWrapSlots: _rlpWrapSlots
                )
            ),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                // Each batch Dispose builds sorted SST file(s) from its buffer and ingests them (metadata-only add,
                // bypassing the memtable), then the pointer advances. Every batch Dispose does disk I/O and can
                // throw; dispose all of them and the snapshot regardless of failure so nothing leaks, and surface
                // the first failure so the currentState pointer is NOT advanced past a partial persist (recovery
                // then re-executes from the previous pointer). NOTE: per-CF ingest is not yet crash-atomic with the
                // pointer write; full atomicity needs the multi-CF rocksdb_ingest_external_files (follow-up).
                try
                {
                    IWriteBatch[] batches = [accountBatch, storageBatch, stateTopNodesBatch, stateNodesBatch, storageNodesBatch, fallbackNodesBatch];
                    ExceptionDispatchInfo? firstFailure = null;
                    foreach (IWriteBatch batch in batches)
                    {
                        try { batch.Dispose(); }
                        catch (Exception e) { firstFailure ??= ExceptionDispatchInfo.Capture(e); }
                    }
                    firstFailure?.Throw();

                    using (IColumnsWriteBatch<FlatDbColumns> metaBatch = db.StartWriteBatch())
                    {
                        BasePersistence.SetCurrentState(metaBatch.GetColumnBatch(FlatDbColumns.Metadata), toCopy);
                        if (_rlpWrapSlots)
                            BasePersistence.RecordLayoutOnFirstBatch(metaBatch.GetColumnBatch(FlatDbColumns.Metadata), ref _layoutPersisted, FlatLayout.Flat);
                    }

                    db.Flush(onlyWal: true);
                }
                finally
                {
                    dbSnap.Dispose();
                }
            })
        );
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

        if (_useSstIngestion && from != StateId.Sync && to != StateId.Sync
            && db.GetColumnDb(FlatDbColumns.Account) is ISstIngestible)
        {
            return CreateIngestWriteBatch(dbSnap, to);
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
