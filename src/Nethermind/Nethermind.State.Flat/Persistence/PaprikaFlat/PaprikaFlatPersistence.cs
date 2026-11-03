// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Paprika;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>
/// <see cref="IPersistence"/> of <see cref="FlatLayout.PaprikaFlat"/>: the flat accounts and storage, the state pointer
/// and the trie node log's confirmed versions commit atomically to the Paprika file of <see cref="PaprikaFlatStore"/>,
/// while trie nodes stay in the RocksDB trie columns behind the trie node log.
/// </summary>
/// <remarks>
/// A write batch commits its trie nodes to the log and makes them durable, then commits its flat writes, its state pointer
/// and the log's versions in one Paprika commit, and only then confirms the log, which may merge them into RocksDB from
/// then on. A sync or import batch drains the log first and writes its trie nodes to RocksDB directly. Range operations act
/// on the state as it is when the batch commits, not when it is created; see <see cref="PaprikaWriteBuffer"/>.
/// </remarks>
public sealed class PaprikaFlatPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly PaprikaFlatStore _store;
    private readonly ITrieNodeLog _trieNodeLog;
    private readonly WriteBufferAdjuster _adjuster;

    /// <summary>Opens the Paprika file of <paramref name="store"/>, stamping a fresh one.</summary>
    /// <exception cref="InvalidConfigurationException"><paramref name="trieNodeLog"/> is <see cref="NullTrieNodeLog"/>.</exception>
    public PaprikaFlatPersistence(IColumnsDb<FlatDbColumns> db, PaprikaFlatStore store, ITrieNodeLog trieNodeLog, IFlatDbConfig config)
    {
        if (trieNodeLog is NullTrieNodeLog)
        {
            throw new InvalidConfigurationException(
                $"FlatDb.Layout={FlatLayout.PaprikaFlat} requires FlatDb.{nameof(IFlatDbConfig.TrieNodeLogEnabled)}: its Paprika commits confirm the trie node log.",
                -1);
        }

        _db = db;
        _store = store;
        _trieNodeLog = trieNodeLog;
        _adjuster = new WriteBufferAdjuster(db, config.PersistenceWriteBufferFloor);
        // The log has recorded its shards by now, so a file still absent at the next start would read as lost.
        store.EnsureOpen();
    }

    /// <inheritdoc/>
    /// <remarks>Makes the trie node log's merges and the Paprika file durable.</remarks>
    public void Flush()
    {
        _trieNodeLog.Drain();
        _db.Flush();
        _store.Flush();
    }

    /// <inheritdoc/>
    public void Clear()
    {
        // The wipe is on record before the log drops generations the database still counts on.
        _store.MarkWipeStarted();
        _trieNodeLog.Clear();
        _store.WipeState();
    }

    /// <inheritdoc/>
    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        ITrieNodeLog.IView view = _trieNodeLog.OpenView(_db, flags);
        try
        {
            // The Paprika root the log bound the view to, opened after its RocksDB snapshot.
            PaprikaFlatStore.Snapshot snapshot = PaprikaFlatStore.SnapshotOf(view.Confirmation);
            BaseTriePersistence.Reader trieReader = new(
                view.GetColumn(FlatDbColumns.StateTopNodes),
                view.GetColumn(FlatDbColumns.StateNodes),
                view.GetColumn(FlatDbColumns.StorageNodes),
                view.GetColumn(FlatDbColumns.FallbackNodes));

            return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<PaprikaFlatReader>, BaseTriePersistence.Reader>(
                new BasePersistence.ToHashedFlatReader<PaprikaFlatReader>(new PaprikaFlatReader(snapshot.Reader)),
                trieReader,
                BasePersistence.ReadCurrentState(snapshot.Metadata),
                view);
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        bool sync = from == StateId.Sync || to == StateId.Sync;
        // Sync and import batches scan the trie columns for range deletes, so the log is merged into RocksDB before
        // the snapshot they scan is taken, and they bypass it.
        bool bypass = sync || flags.HasFlag(WriteFlags.DisableWAL);
        if (bypass) _trieNodeLog.Drain();

        if (!sync)
        {
            StateId currentState = BasePersistence.ReadCurrentState(_store.Committed);
            if (currentState != from)
                throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        IColumnDbSnapshot<FlatDbColumns> dbSnap = _db.CreateSnapshot();
        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        PaprikaWriteBuffer buffer = new(from, to);
        ITrieNodeLog.IWriteBatch logBatch;
        try
        {
            logBatch = _trieNodeLog.StartWriteBatch(buffer.Metadata, bypass);
        }
        catch
        {
            batch.Clear();
            batch.Dispose();
            dbSnap.Dispose();
            buffer.Dispose();
            throw;
        }

        BaseTriePersistence.WriteBatch trieWriteBatch = new(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            logBatch.Wrap(FlatDbColumns.StateTopNodes, _adjuster.Wrap(batch, FlatDbColumns.StateTopNodes, flags)),
            logBatch.Wrap(FlatDbColumns.StateNodes, _adjuster.Wrap(batch, FlatDbColumns.StateNodes, flags)),
            logBatch.Wrap(FlatDbColumns.StorageNodes, _adjuster.Wrap(batch, FlatDbColumns.StorageNodes, flags)),
            logBatch.Wrap(FlatDbColumns.FallbackNodes, _adjuster.Wrap(batch, FlatDbColumns.FallbackNodes, flags)),
            flags);

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<PaprikaFlatWriteBatch>, BaseTriePersistence.WriteBatch>(
            new BasePersistence.ToHashedWriteBatch<PaprikaFlatWriteBatch>(new PaprikaFlatWriteBatch(buffer)),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() => Commit(buffer, batch, logBatch, dbSnap, sync, bypass)));
    }

    private void Commit(PaprikaWriteBuffer buffer, IColumnsWriteBatch<FlatDbColumns> batch, ITrieNodeLog.IWriteBatch logBatch, IColumnDbSnapshot<FlatDbColumns> dbSnap, bool sync, bool bypass)
    {
        bool batchWritten = false;
        try
        {
            // The log's records are durable, and its versions in the buffer, before Paprika commits them with the state
            // pointer; the log is confirmed only once that commit is durable.
            logBatch.Commit();
            if (!sync) BasePersistence.SetCurrentState(buffer.Metadata, buffer.To);
            batchWritten = true;
            // The trie nodes of a batch that bypasses the log; empty otherwise.
            batch.Dispose();
            _store.Commit(buffer, sync ? CommitOptions.FlushDataOnly : CommitOptions.FlushDataAndRoot, exclusive: !bypass);
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
            buffer.Dispose();
        }
    }
}
