// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat.Persistence;

public class RocksdbPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    private readonly Configuration _configuration;
    private readonly SegmentedBloom _bloomFilter;

    public record Configuration(bool FlatInTrie = false)
    {
    }

    public RocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        [KeyFilter(DbNames.Flat)] SegmentedBloom bloomFilter,
        Configuration configuration)
    {
        _configuration = configuration;
        _db = db;
        _bloomFilter = bloomFilter;
    }

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[] bytes = kv.Get(CurrentStateKey);
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
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], stateId.blockNumber);
        stateId.stateRoot.BytesAsSpan.CopyTo(bytes[8..]);

        kv.PutSpan(CurrentStateKey, bytes);
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        var snapshot = _db.CreateSnapshot();
        var trieReader = new TriePersistence.Reader(
            snapshot.GetColumn(FlatDbColumns.StateTopNodes),
            snapshot.GetColumn(FlatDbColumns.StateNodes),
            snapshot.GetColumn(FlatDbColumns.StorageNodes)
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

        var flatReader = new HashedFlatPersistence.Reader(
            state,
            storage,
            _bloomFilter
        );

        return new BaseRocksdbPersistence.PersistenceReader<HashedFlatPersistence.Reader, TriePersistence.Reader>(
            flatReader,
            trieReader,
            currentState,
            new Reactive.AnonymousDisposable(() =>
            {
                snapshot.Dispose();
            })
        );
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
        IWriteOnlyKeyValueStore state;
        IWriteOnlyKeyValueStore storage;
        if (_configuration.FlatInTrie)
        {
            state = batch.GetColumnBatch(FlatDbColumns.StateNodes);
            storage = batch.GetColumnBatch(FlatDbColumns.StorageNodes);
        }
        else
        {
            state = batch.GetColumnBatch(FlatDbColumns.Account);
            storage = batch.GetColumnBatch(FlatDbColumns.Storage);
        }

        var flatWriter = new HashedFlatPersistence.WriteBatch(
            ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.Storage)),
            state,
            storage,
            flags,
            _bloomFilter
        );

        var trieWriteBatch = new TriePersistence.WriteBatch(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
            flags);

        return new BaseRocksdbPersistence.WriteBatch<HashedFlatPersistence.WriteBatch, TriePersistence.WriteBatch>(
            flatWriter,
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to);
                batch.Dispose();
                dbSnap.Dispose();
                if (!flags.HasFlag(WriteFlags.DisableWAL))
                {
                    _bloomFilter.Flush();
                }
            })
        );
    }
}
