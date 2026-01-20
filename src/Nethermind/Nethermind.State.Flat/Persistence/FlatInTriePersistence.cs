// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Persistence implementation that stores flat state data in the trie node columns (StateNodes/StorageNodes)
/// instead of separate Account/Storage columns.
/// </summary>
public class FlatInTriePersistence(IColumnsDb<FlatDbColumns> db) : IPersistence, IPersistenceWithConcurrentTrie
{
    public IPersistence.IPersistenceReader CreateReader()
    {
        var snapshot = db.CreateSnapshot();
        try
        {
            var trieReader = new BaseTriePersistence.Reader(
                snapshot.GetColumn(FlatDbColumns.StateTopNodes),
                snapshot.GetColumn(FlatDbColumns.StateNodes),
                snapshot.GetColumn(FlatDbColumns.StorageNodes),
                snapshot.GetColumn(FlatDbColumns.FallbackNodes)
            );

            var currentState = RocksdbPersistence.ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

            return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>, BaseTriePersistence.Reader>(
                new BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>(
                    new BaseFlatPersistence.Reader(
                        snapshot.GetColumn(FlatDbColumns.StateNodes),
                        snapshot.GetColumn(FlatDbColumns.StorageNodes)
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

    public IPersistence.IWriteBatch CreateWriteBatch(StateId from, StateId to, WriteFlags flags)
    {
        var dbSnap = db.CreateSnapshot();
        var currentState = RocksdbPersistence.ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();

        var trieWriteBatch = new BaseTriePersistence.WriteBatch(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
            batch.GetColumnBatch(FlatDbColumns.FallbackNodes),
            flags);

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(
            new BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>(
                new BaseFlatPersistence.WriteBatch(
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
                    batch.GetColumnBatch(FlatDbColumns.StateNodes),
                    batch.GetColumnBatch(FlatDbColumns.StorageNodes),
                    flags
                )
            ),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                RocksdbPersistence.SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to);
                batch.Dispose();
                dbSnap.Dispose();
                if (!flags.HasFlag(WriteFlags.DisableWAL))
                {
                    db.Flush(onlyWal: true);
                }
            })
        );
    }

    public IPersistenceWithConcurrentTrie.IWriteBatch CreateTrieWriteBatch(WriteFlags flags = WriteFlags.None)
    {
        var dbSnap = db.CreateSnapshot();
        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();
        var trieWriteBatch = new BaseTriePersistence.WriteBatch(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
            batch.GetColumnBatch(FlatDbColumns.FallbackNodes),
            flags);

        return new ConcurrentTrieWriter(trieWriteBatch, dbSnap, batch);
    }

    private class ConcurrentTrieWriter(BaseTriePersistence.WriteBatch trieWriteBatch, IColumnDbSnapshot<FlatDbColumns> dbSnap, IColumnsWriteBatch<FlatDbColumns> batch) : IPersistenceWithConcurrentTrie.IWriteBatch
    {
        public void Dispose()
        {
            dbSnap.Dispose();
            batch.Dispose();
        }

        public void SetStateTrieNode(in TreePath path, TrieNode tnValue)
        {
            trieWriteBatch.SetStateTrieNode(path, tnValue);
        }

        public void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue)
        {
            trieWriteBatch.SetStorageTrieNode(address, path, tnValue);
        }
    }
}
