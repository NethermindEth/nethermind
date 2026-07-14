// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence;

public class RocksDbPersistence : IPersistence
{
    private static readonly FlatDbColumns[] IngestColumns =
    [
        FlatDbColumns.Account,
        FlatDbColumns.Storage,
        FlatDbColumns.StateTopNodes,
        FlatDbColumns.StateNodes,
        FlatDbColumns.StorageNodes,
        FlatDbColumns.FallbackNodes,
    ];

    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly bool _storeSupportsIngest;
    private readonly WriteBufferAdjuster _adjuster;
    private int _layoutPersisted;
    private readonly bool _rlpWrapSlots;
    private readonly bool _useSstIngestion;
    private readonly ILogger _logger;
    // Gates new reader snapshots out of the ingest commit window (per-CF ingests + pointer write), which is
    // not a single RocksDB sequence number the way a normal WriteBatch commit is.
    private readonly ReaderWriterLockSlim _ingestGate = new();

    public RocksDbPersistence(IColumnsDb<FlatDbColumns> db, ILogManager logManager, IFlatDbConfig? config = null)
    {
        _db = db;
        _logger = logManager.GetClassLogger<RocksDbPersistence>();
        _adjuster = new(db, config?.PersistenceWriteBufferFloor ?? WriteBufferAdjuster.DefaultWriteBufferFloor);
        _layoutPersisted = BasePersistence.ValidateLayoutReturnFlag(db, FlatLayout.Flat);
        _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), _logger);
        _useSstIngestion = config?.PersistViaSstIngestion ?? false;

        // Startup recovery is an explicit step gated on the flag: with SST ingestion disabled a node must neither
        // roll a stale marker forward nor dir-scan the staging directory, so the short-circuit skips it entirely.
        // When enabled, RecoverInterruptedIngest also reports whether the store supports ingestion at all.
        _storeSupportsIngest = _useSstIngestion && RecoverInterruptedIngest(db, logManager);
    }

    public void Flush() => _db.Flush();

    public void Clear() => BasePersistence.ClearAllColumns(_db);

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        IColumnDbSnapshot<FlatDbColumns> snapshot = CreateGatedSnapshot();
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

    private IColumnDbSnapshot<FlatDbColumns> CreateGatedSnapshot()
    {
        if (!_useSstIngestion) return _db.CreateSnapshot();

        _ingestGate.EnterReadLock();
        try
        {
            return _db.CreateSnapshot();
        }
        finally
        {
            _ingestGate.ExitReadLock();
        }
    }

    private IPersistence.IWriteBatch CreateIngestWriteBatch(IColumnDbSnapshot<FlatDbColumns> dbSnap, StateId to)
    {
        ISstIngestWriteBatch[] batches = new ISstIngestWriteBatch[IngestColumns.Length];
        for (int i = 0; i < IngestColumns.Length; i++)
            batches[i] = ((ISstIngestible)_db.GetColumnDb(IngestColumns[i])).StartSstIngestBatch();

        ISstIngestWriteBatch BatchFor(FlatDbColumns column) => batches[Array.IndexOf(IngestColumns, column)];

        BaseTriePersistence.WriteBatch trieWriteBatch = new(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            BatchFor(FlatDbColumns.StateTopNodes),
            BatchFor(FlatDbColumns.StateNodes),
            BatchFor(FlatDbColumns.StorageNodes),
            BatchFor(FlatDbColumns.FallbackNodes),
            WriteFlags.None);

        StateId toCopy = to;

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(
            new BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>(
                new BaseFlatPersistence.WriteBatch(
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Account),
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
                    BatchFor(FlatDbColumns.Account),
                    BatchFor(FlatDbColumns.Storage),
                    WriteFlags.None,
                    rlpWrapSlots: _rlpWrapSlots
                )
            ),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                try
                {
                    CommitIngest(batches, toCopy);
                }
                finally
                {
                    foreach (ISstIngestWriteBatch batch in batches) batch.Dispose();
                    dbSnap.Dispose();
                }
            })
        );
    }

    private void CommitIngest(ISstIngestWriteBatch[] batches, in StateId to)
    {
        int columnsIngested = 0;
        try
        {
            List<string> stagedFiles = [];
            foreach (ISstIngestWriteBatch batch in batches)
                stagedFiles.AddRange(batch.SealToStagedFiles());

            // Redo marker: durable before the first ingest so a crash anywhere below rolls forward to `to`
            // on reopen; the pointer therefore never claims a state some column lacks.
            using (IColumnsWriteBatch<FlatDbColumns> markerBatch = _db.StartWriteBatch())
            {
                IWriteBatch metadata = markerBatch.GetColumnBatch(FlatDbColumns.Metadata);
                BasePersistence.SetIngestMarker(metadata, to, stagedFiles);
                if (_rlpWrapSlots)
                    BasePersistence.RecordLayoutOnFirstBatch(metadata, ref _layoutPersisted, FlatLayout.Flat);
            }
            _db.Flush(onlyWal: true);

            _ingestGate.EnterWriteLock();
            try
            {
                foreach (ISstIngestWriteBatch batch in batches)
                {
                    batch.IngestStagedFiles();
                    columnsIngested++;
                }

                using (IColumnsWriteBatch<FlatDbColumns> pointerBatch = _db.StartWriteBatch())
                {
                    IWriteBatch metadata = pointerBatch.GetColumnBatch(FlatDbColumns.Metadata);
                    BasePersistence.SetCurrentState(metadata, to);
                    BasePersistence.ClearIngestMarker(metadata);
                }
                _db.Flush(onlyWal: true);
            }
            finally
            {
                _ingestGate.ExitWriteLock();
            }
        }
        catch (Exception e)
        {
            // Rollback is only safe while no column has been ingested; after that the already-live columns
            // cannot be un-ingested, so the marker and remaining staged files must survive for a retried
            // persist or startup recovery to roll the commit forward.
            if (columnsIngested == 0)
            {
                RollbackFailedIngest(batches);
            }
            else if (_logger.IsError)
            {
                _logger.Error($"Persist to {to} failed after {columnsIngested} of {batches.Length} column ingests; keeping the redo marker and staged files for roll-forward", e);
            }
            throw;
        }

        // The L0 throttle stays outside the gate so reader snapshot creation is not stalled behind compaction.
        // The persist is already durable here (pointer advanced, marker cleared), so this backpressure is
        // best-effort: a throw must not surface a committed persist as a failure to the caller.
        try
        {
            foreach (FlatDbColumns column in IngestColumns)
                ((ISstIngestible)_db.GetColumnDb(column)).WaitForIngestCompactionHeadroom();
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Ingest compaction backpressure after persisting {to} failed; the state is already durable, continuing. {e}");
        }
    }

    private void RollbackFailedIngest(ISstIngestWriteBatch[] batches)
    {
        try
        {
            // The marker must be gone before staged files are deleted: a marker surviving its files would
            // roll the pointer forward past missing data on the next open.
            using (IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch())
                BasePersistence.ClearIngestMarker(batch.GetColumnBatch(FlatDbColumns.Metadata));
            _db.Flush(onlyWal: true);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Failed to clear the SST ingest marker after a failed persist; keeping staged files for startup roll-forward", e);
            return;
        }

        foreach (ISstIngestWriteBatch batch in batches)
            batch.DeleteStagedFiles();
    }

    /// <summary>
    /// Startup recovery for the SST-ingest persist path. A pending marker means the process died between the
    /// first ingest and the pointer advance: re-ingest the staged files that remain (move-ingest consumed the
    /// rest) and complete the pointer write. Without a marker, leftover staged files are orphans and are removed.
    /// Returns whether the store supports SST ingestion at all.
    /// </summary>
    private static bool RecoverInterruptedIngest(IColumnsDb<FlatDbColumns> db, ILogManager logManager)
    {
        if (db.GetColumnDb(FlatDbColumns.Account) is not ISstIngestible ingestible) return false;

        ILogger logger = logManager.GetClassLogger<RocksDbPersistence>();
        string stagingDir = ingestible.IngestStagingDir;

        if (BasePersistence.ReadIngestMarker(db.GetColumnDb(FlatDbColumns.Metadata)) is { } pending)
            RollForwardPendingIngest(db, stagingDir, pending, logger);

        if (Directory.Exists(stagingDir))
        {
            string[] orphans = Directory.GetFiles(stagingDir);
            foreach (string orphan in orphans) File.Delete(orphan);
            if (orphans.Length > 0 && logger.IsInfo)
                logger.Info($"Deleted {orphans.Length} orphaned SST staging file(s) from {stagingDir}");
        }

        return true;
    }

    private static void RollForwardPendingIngest(IColumnsDb<FlatDbColumns> db, string stagingDir, (StateId To, string[] Files) pending, ILogger logger)
    {
        Dictionary<FlatDbColumns, List<string>> byColumn = [];
        foreach (string name in pending.Files)
        {
            int cut = name.LastIndexOf('_');
            if (cut <= 0 || !Enum.TryParse(name.AsSpan(0, cut), out FlatDbColumns column) || !Enum.IsDefined(column) || column == FlatDbColumns.Metadata)
                throw new InvalidOperationException($"Flat DB SST ingest marker references unrecognized staged file '{name}'");

            string path = Path.Combine(stagingDir, name);
            // A missing file was already ingested: move-ingest deletes its source on success.
            if (!File.Exists(path)) continue;

            if (!byColumn.TryGetValue(column, out List<string>? files)) byColumn[column] = files = [];
            files.Add(path);
        }

        int reingested = 0;
        foreach ((FlatDbColumns column, List<string> files) in byColumn)
        {
            ((ISstIngestible)db.GetColumnDb(column)).IngestStagedFiles(files);
            reingested += files.Count;
        }

        using (IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch())
        {
            IWriteBatch metadata = batch.GetColumnBatch(FlatDbColumns.Metadata);
            BasePersistence.SetCurrentState(metadata, pending.To);
            BasePersistence.ClearIngestMarker(metadata);
        }
        db.Flush(onlyWal: true);

        if (logger.IsInfo)
            logger.Info($"Rolled interrupted flat DB persist forward to {pending.To}: re-ingested {reingested} of {pending.Files.Length} staged SST file(s)");
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        IColumnDbSnapshot<FlatDbColumns> dbSnap = _db.CreateSnapshot();
        StateId currentState = BasePersistence.ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (from != StateId.Sync && to != StateId.Sync && currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        if (_useSstIngestion && _storeSupportsIngest && from != StateId.Sync && to != StateId.Sync)
        {
            return CreateIngestWriteBatch(dbSnap, to);
        }

        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();

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
                    _db.Flush(onlyWal: true);
                }
            })
        );
    }

}
