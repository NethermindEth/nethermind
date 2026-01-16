// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class RocksdbPersistence : IPersistence, IPersistenceWithConcurrentTrie
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    private readonly Configuration _configuration;
    private readonly ILogger _logger;

    public record Configuration(bool FlatInTrie = false)
    {
    }

    public RocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        Configuration configuration,
        ILogManager logManager)
    {
        _configuration = configuration;
        _db = db;
        _logger = logManager.GetClassLogger<RocksdbPersistence>();
    }

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(CurrentStateKey);
        if (bytes is null || bytes.Length == 0)
        {
            return new StateId(-1, Keccak.EmptyTreeHash);
        }

        long blockNumber = BinaryPrimitives.ReadInt64BigEndian(bytes);
        Hash256 stateHash = new Hash256(bytes[8..]);
        return new StateId(blockNumber, stateHash);
    }

    internal static void SetCurrentState(IWriteOnlyKeyValueStore kv, StateId stateId)
    {
        Span<byte> bytes = stackalloc byte[8 + 32];
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], stateId.BlockNumber);
        stateId.StateRoot.BytesAsSpan.CopyTo(bytes[8..]);

        kv.PutSpan(CurrentStateKey, bytes);
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        var snapshot = _db.CreateSnapshot();
        try
        {
            var trieReader = new BaseTriePersistence.Reader(
                snapshot.GetColumn(FlatDbColumns.StateTopNodes),
                snapshot.GetColumn(FlatDbColumns.StateNodes),
                snapshot.GetColumn(FlatDbColumns.StorageNodes),
                snapshot.GetColumn(FlatDbColumns.FallbackNodes)
            );

            var currentState = ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

            IReadOnlyKeyValueStore state;
            IReadOnlyKeyValueStore storage;
            if (_configuration.FlatInTrie)
            {
                state = snapshot.GetColumn(FlatDbColumns.StateNodes);
                storage = snapshot.GetColumn(FlatDbColumns.StorageNodes);
            }
            else
            {
                state = snapshot.GetColumn(FlatDbColumns.Account);
                storage = snapshot.GetColumn(FlatDbColumns.Storage);
            }

            return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>, BaseTriePersistence.Reader>(
                new BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>(
                    new BaseFlatPersistence.Reader(
                        state,
                        storage
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
        var dbSnap = _db.CreateSnapshot();
        var currentState = ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        ISortedKeyValueStore storageSnapshot;
        IWriteOnlyKeyValueStore state;
        IWriteOnlyKeyValueStore storage;
        if (_configuration.FlatInTrie)
        {
            state = batch.GetColumnBatch(FlatDbColumns.StateNodes);
            storage = batch.GetColumnBatch(FlatDbColumns.StorageNodes);
            storageSnapshot = (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes);
        }
        else
        {
            state = batch.GetColumnBatch(FlatDbColumns.Account);
            storage = batch.GetColumnBatch(FlatDbColumns.Storage);
            storageSnapshot = (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage);
        }

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
                    storageSnapshot,
                    state,
                    storage,
                    flags
                )
            ),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to);
                batch.Dispose();
                dbSnap.Dispose();
                if (!flags.HasFlag(WriteFlags.DisableWAL))
                {
                    _db.Flush(onlyWal: true);
                }
            })
        );
    }

    public bool WarmUpWhole(CancellationToken cancellation)
    {
        _logger.Warn("Warming up storage...");
        var storageDb = (ISortedKeyValueStore) _db.GetColumnDb(FlatDbColumns.Storage);
        ShardedParallelWarmup(storageDb, cancellation);

        if (cancellation.IsCancellationRequested) return false;
        _logger.Warn("Warming up account...");
        var accountDb = (ISortedKeyValueStore) _db.GetColumnDb(FlatDbColumns.Account);
        ShardedParallelWarmup(accountDb, cancellation);

        _logger.Warn("Warmup complete");
        return true;
    }

    private void ShardedParallelWarmup(ISortedKeyValueStore kvStore, CancellationToken cancellation)
    {
        long num = 0;
        Parallel.For(0, 255, idx =>
        {
            if (cancellation.IsCancellationRequested) return;
            byte[] firstKey = [(byte)idx];
            byte[] secondKey = [];
            if (idx == 255)
            {
                secondKey = [(byte)idx, (byte)idx];
            }
            else
            {
                secondKey = [(byte)(idx + 1)];
            }

            long localCount = 0;
            using (var view = kvStore.GetViewBetween(firstKey, secondKey))
            {
                while (view.MoveNext())
                {
                    localCount++;
                    if (localCount % 1000 == 0)
                    {
                        // Check every 1000 key only, in case its heavy
                        if (cancellation.IsCancellationRequested) return;
                    }

                    long cur = Interlocked.Increment(ref num);
                    if (cur % 1_000_000 == 0)
                    {
                        if (cancellation.IsCancellationRequested) return;
                        _logger.Warn($"{cur:N0} keys");
                    }
                }
            }
        });
    }

    public IPersistenceWithConcurrentTrie.IWriteBatch CreateTrieWriteBatch(WriteFlags flags = WriteFlags.None)
    {
        var dbSnap = _db.CreateSnapshot();
        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
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
