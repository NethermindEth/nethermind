// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using LightningDB;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// This persistence used LMDB for the flat portion and rocksdb for the trie portion in hope that LMDB faster readonly
/// will make things faster. No WAL is implemented to sync the two db, so it is crash prone.
/// </summary>
public class LMDBPersistence : IPersistence
{
    public bool SupportConcurrentWrites => false;

    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    private const int StateKeyPrefixLength = 20;

    private const int StorageHashPrefixLength = 20; // Store prefix of the 32 byte of the storage. Reduces index size.
    private const int StorageSlotKeySize = 32;
    private const int StorageKeyLength = StorageHashPrefixLength + StorageSlotKeySize;
    private const int FullPathLength = 32;
    private const int PathLengthLength = 1;
    private const int StorageNodesKeyLength = StorageHashPrefixLength + FullPathLength + PathLengthLength;

    private readonly LightningEnvironment _lmdbEnv;

    public LMDBPersistence(IColumnsDb<FlatDbColumns> db, LightningEnvironment lmdbEnv)
    {
        _db = db;
        _lmdbEnv = lmdbEnv;
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
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], stateId.blockNumber);
        stateId.stateRoot.BytesAsSpan.CopyTo(bytes[8..]);

        kv.PutSpan(CurrentStateKey, bytes);
    }

    private static ReadOnlySpan<byte> EncodeAccountKey(Span<byte> buffer, in ValueHash256 addr)
    {
        return addr.BytesAsSpan[..StateKeyPrefixLength];
    }

    internal static ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, in ValueHash256 addr, in ValueHash256 slot)
    {
        addr.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        slot.BytesAsSpan.CopyTo(buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);

        return buffer[..StorageKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeStorageKeyHashed(Span<byte> buffer, in ValueHash256 addrHash, in ValueHash256 slotHash)
    {
        addrHash.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        slotHash.Bytes.CopyTo(buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);
        return buffer[..StorageKeyLength];
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        var lmdbTx = _lmdbEnv.BeginTransaction(TransactionBeginFlags.ReadOnly);
        var snapshot = _db.CreateSnapshot();
        var trieReader = new BaseTriePersistence.Reader(
            snapshot.GetColumn(FlatDbColumns.StateTopNodes),
            snapshot.GetColumn(FlatDbColumns.StateNodes),
            snapshot.GetColumn(FlatDbColumns.StorageNodes)
        );

        var state = lmdbTx.OpenDatabase(FlatDbColumns.Account.ToString());
        var storage = lmdbTx.OpenDatabase(FlatDbColumns.Storage.ToString());

        var flatReader = new BasePersistence.ToHashedFlatReader<LMDBFlatReader>(
            new LMDBFlatReader(
                state,
                storage,
                lmdbTx
            )
        );

        var currentState = ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));
        return new BasePersistence.Reader<BasePersistence.ToHashedFlatReader<LMDBFlatReader>, BaseTriePersistence.Reader>(
            flatReader,
            trieReader,
            currentState,
            new Reactive.AnonymousDisposable(() =>
            {
                snapshot.Dispose();
                lmdbTx.Dispose();
            })
        );
    }

    private int _hasWriteBatch = 0;
    private void MarkWriteBatchComplete()
    {
        Interlocked.CompareExchange(ref _hasWriteBatch, 0, 1);
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

        if (Interlocked.CompareExchange(ref _hasWriteBatch, 1, 0) != 0)
        {
            throw new InvalidOperationException("Previous write batch not completed yet");
        }

        var batch = _db.StartWriteBatch();
        var lmdbTx = _lmdbEnv.BeginTransaction((flags & WriteFlags.DisableWAL) != 0 ? TransactionBeginFlags.NoSync : TransactionBeginFlags.NoMetaSync);
        var state = lmdbTx.OpenDatabase(FlatDbColumns.Account.ToString());
        var storage = lmdbTx.OpenDatabase(FlatDbColumns.Storage.ToString());

        var flatWriter = new BasePersistence.ToHashedWriteBatch<LMDBFlatWriter>(
            new LMDBFlatWriter(
                state,
                storage,
                lmdbTx
            )
        );

        var trieWriteBatch = new BaseTriePersistence.WriteBatch(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
            flags);

        return new BasePersistence.WriteBatch<BasePersistence.ToHashedWriteBatch<LMDBFlatWriter>, BaseTriePersistence.WriteBatch>(
            flatWriter,
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to);
                batch.Dispose();
                dbSnap.Dispose();

                lmdbTx.Commit();
                lmdbTx.Dispose();

                if (!flags.HasFlag(WriteFlags.DisableWAL))
                {
                    MarkWriteBatchComplete();
                }
            })
        );

    }

    private readonly struct LMDBFlatWriter(
        LightningDatabase state,
        LightningDatabase storage,
        LightningTransaction _lmdbTx
    ) : BasePersistence.IHashedFlatWriteBatch
    {
        public void RemoveAccount(in ValueHash256 address)
        {
            _lmdbTx.Delete(state, EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], address));
        }

        public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value)
        {
            _lmdbTx.Put(state, EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], address), value);
        }

        public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, ReadOnlySpan<byte> value)
        {
            _lmdbTx.Put(storage, EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], address, slotHash), value);
        }

        public void RemoveStorage(in ValueHash256 address, in ValueHash256 slotHash)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKey(stackalloc byte[StorageKeyLength], address, slotHash);
            _lmdbTx.Delete(storage, theKey);
        }

        public int SelfDestruct(in ValueHash256 accountPath)
        {
            Span<byte> firstKey = stackalloc byte[StorageHashPrefixLength]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[StorageNodesKeyLength];

            // for storage the prefix might change depending on the encoding
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            EncodeAccountKey(firstKey, accountPath);
            EncodeAccountKey(lastKey, accountPath);

            using var storageCursor = _lmdbTx.CreateCursor(storage);
            storageCursor.SetRange(firstKey);

            int removedEntry = 0;

            while (true)
            {
                (MDBResultCode resultCode, MDBValue key, MDBValue value) = storageCursor.GetCurrent();
                if (resultCode != MDBResultCode.Success) break;

                // Out of range
                if (Bytes.BytesComparer.Compare(key.AsSpan(), lastKey) >= 0) break;

                _lmdbTx.Delete(storage, key.AsSpan());

                storageCursor.Next();
            }

            return removedEntry;
        }
    }

    private readonly struct LMDBFlatReader(
        LightningDatabase state,
        LightningDatabase storage,
        LightningTransaction lmdbTx
    ) : BasePersistence.IHashedFlatReader
    {
        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer)
        {
            (MDBResultCode resultCode, MDBValue key, MDBValue valueMdb) = lmdbTx.Get(state, address.Bytes[..StateKeyPrefixLength]);
            if (resultCode != MDBResultCode.Success) throw new Exception($"Read account raw failed with result code {resultCode}");
            valueMdb.AsSpan().CopyTo(outBuffer);
            return valueMdb.AsSpan().Length;
        }

        public int GetStorage(in ValueHash256 address, in ValueHash256 slot, Span<byte> outBuffer)
        {
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = EncodeStorageKeyHashed(keySpan, address, slot);
            (MDBResultCode resultCode, MDBValue key, MDBValue valueMdb) = lmdbTx.Get(storage, storageKey);
            if (resultCode != MDBResultCode.Success) throw new Exception($"Read storage raw failed with result code {resultCode}");
            valueMdb.AsSpan().CopyTo(outBuffer);
            return valueMdb.AsSpan().Length;
        }
    }
}
