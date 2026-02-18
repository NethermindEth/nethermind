// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class RocksDbPersistence(IColumnsDb<FlatDbColumns> db) : IPersistence
{
    private const long MinWriteBufferSize = 16L * 1024 * 1024;   // 16 MB floor
    private const long MaxWriteBufferSize = 256L * 1024 * 1024;  // 256 MB cap

    private static readonly byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();
    private readonly Dictionary<FlatDbColumns, long> _lastWriteBufferSize = new();

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(CurrentStateKey);
        return bytes is null || bytes.Length == 0
            ? new StateId(-1, ValueKeccak.EmptyTreeHash)
            : new StateId(BinaryPrimitives.ReadInt64BigEndian(bytes), new ValueHash256(bytes[8..]));
    }

    internal static void SetCurrentState(IWriteOnlyKeyValueStore kv, in StateId stateId)
    {
        Span<byte> bytes = stackalloc byte[8 + 32];
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], stateId.BlockNumber);
        stateId.StateRoot.BytesAsSpan.CopyTo(bytes[8..]);

        kv.PutSpan(CurrentStateKey, bytes);
    }

    public void Flush() => db.Flush();

    public IPersistence.IPersistenceReader CreateReader()
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

            StateId currentState = ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

            return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>, BaseTriePersistence.Reader>(
                new BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader>(
                    new BaseFlatPersistence.Reader(
                        (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Account),
                        (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Storage),
                        isPreimageMode: false
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
        StateId currentState = ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();

        bool shouldCount = !flags.HasFlag(WriteFlags.DisableWAL);

        CountingWriteBatch? accountCounter = shouldCount ? new(batch.GetColumnBatch(FlatDbColumns.Account)) : null;
        CountingWriteBatch? storageCounter = shouldCount ? new(batch.GetColumnBatch(FlatDbColumns.Storage)) : null;
        CountingWriteBatch? stateTopNodesCounter = shouldCount ? new(batch.GetColumnBatch(FlatDbColumns.StateTopNodes)) : null;
        CountingWriteBatch? stateNodesCounter = shouldCount ? new(batch.GetColumnBatch(FlatDbColumns.StateNodes)) : null;
        CountingWriteBatch? storageNodesCounter = shouldCount ? new(batch.GetColumnBatch(FlatDbColumns.StorageNodes)) : null;
        CountingWriteBatch? fallbackNodesCounter = shouldCount ? new(batch.GetColumnBatch(FlatDbColumns.FallbackNodes)) : null;

        IWriteOnlyKeyValueStore accountBatch = accountCounter ?? batch.GetColumnBatch(FlatDbColumns.Account);
        IWriteOnlyKeyValueStore storageBatch = storageCounter ?? batch.GetColumnBatch(FlatDbColumns.Storage);
        IWriteOnlyKeyValueStore stateTopNodesBatch = stateTopNodesCounter ?? batch.GetColumnBatch(FlatDbColumns.StateTopNodes);
        IWriteOnlyKeyValueStore stateNodesBatch = stateNodesCounter ?? batch.GetColumnBatch(FlatDbColumns.StateNodes);
        IWriteOnlyKeyValueStore storageNodesBatch = storageNodesCounter ?? batch.GetColumnBatch(FlatDbColumns.StorageNodes);
        IWriteOnlyKeyValueStore fallbackNodesBatch = fallbackNodesCounter ?? batch.GetColumnBatch(FlatDbColumns.FallbackNodes);

        BaseTriePersistence.WriteBatch trieWriteBatch = new(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            stateTopNodesBatch,
            stateNodesBatch,
            storageNodesBatch,
            fallbackNodesBatch,
            flags);

        StateId toCopy = to;

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(
            new BasePersistence.ToHashedWriteBatch<BaseFlatPersistence.WriteBatch>(
                new BaseFlatPersistence.WriteBatch(
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
                    accountBatch,
                    storageBatch,
                    flags
                )
            ),
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), toCopy);
                batch.Dispose();
                dbSnap.Dispose();
                if (shouldCount)
                {
                    AdjustWriteBuffer(FlatDbColumns.Account, accountCounter!.BytesWritten);
                    AdjustWriteBuffer(FlatDbColumns.Storage, storageCounter!.BytesWritten);
                    AdjustWriteBuffer(FlatDbColumns.StateTopNodes, stateTopNodesCounter!.BytesWritten);
                    AdjustWriteBuffer(FlatDbColumns.StateNodes, stateNodesCounter!.BytesWritten);
                    AdjustWriteBuffer(FlatDbColumns.StorageNodes, storageNodesCounter!.BytesWritten);
                    AdjustWriteBuffer(FlatDbColumns.FallbackNodes, fallbackNodesCounter!.BytesWritten);
                    db.Flush(onlyWal: true);
                }
            })
        );
    }

    private void AdjustWriteBuffer(FlatDbColumns column, long bytesWritten)
    {
        if (bytesWritten == 0) return;
        long target = Math.Clamp((long)(bytesWritten * 1.5), MinWriteBufferSize, MaxWriteBufferSize);
        if (_lastWriteBufferSize.TryGetValue(column, out long lastSize)
            && Math.Abs(target - lastSize) <= (long)(lastSize * 0.2))
            return;
        _lastWriteBufferSize[column] = target;
        db.GetColumnDb(column).SetWriteBuffer(target);
    }

    private class CountingWriteBatch(IWriteBatch inner) : IWriteBatch
    {
        public long BytesWritten { get; private set; }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            BytesWritten += key.Length + (value?.Length ?? 0);
            inner.Set(key, value, flags);
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            BytesWritten += key.Length + value.Length;
            inner.PutSpan(key, value, flags);
        }

        public void Remove(ReadOnlySpan<byte> key)
        {
            BytesWritten += key.Length;
            inner.Remove(key);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            BytesWritten += key.Length + value.Length;
            inner.Merge(key, value, flags);
        }

        public void Clear() => inner.Clear();
        public void Dispose() => inner.Dispose();
    }
}
