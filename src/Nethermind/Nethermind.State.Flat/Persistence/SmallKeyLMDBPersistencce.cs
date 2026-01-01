// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using LightningDB;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// This persistence used LMDB for the flat portion and rocksdb for the trie portion in hope that LMDB faster readonly
/// will make things faster. No WAL is implemented to sync the two db, so it is crash prone.
/// </summary>
public class SmallKeyLMDBPersistence : IPersistence, IPersistenceWithConcurrentTrie
{
    public bool SupportConcurrentWrites => false;

    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    private const int StateKeyPrefixLength = 20;

    private const int LMDBStoragePrefixLength = 4; // The address have a lookup on a separate db
    private const int StorageSlotKeySize = 20; // Just use part of the hash
    private const int StorageKeyLength = LMDBStoragePrefixLength + StorageSlotKeySize;

    public const string AddressLookupTableName = "address_lookup";

    private readonly LightningEnvironment _lmdbEnv;

    public SmallKeyLMDBPersistence(IColumnsDb<FlatDbColumns> db, LightningEnvironment lmdbEnv)
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

    internal static ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, MDBValue addr, in ValueHash256 slot)
    {
        addr.AsSpan().CopyTo(buffer);
        slot.BytesAsSpan[..StorageSlotKeySize].CopyTo(buffer[LMDBStoragePrefixLength..]);

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

        var addressLookup = lmdbTx.OpenDatabase(AddressLookupTableName);
        var storage = lmdbTx.OpenDatabase(FlatDbColumns.Storage.ToString());

        var flatReader = new BasePersistence.ToHashedFlatReader<LMDBFlatReader>(
            new LMDBFlatReader(
                snapshot.GetColumn(FlatDbColumns.Account),
                addressLookup,
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
        var lmdbTx = _lmdbEnv.BeginTransaction((flags & WriteFlags.DisableWAL) != 0 ? TransactionBeginFlags.NoSync : TransactionBeginFlags.NoSync);
        var state = batch.GetColumnBatch(FlatDbColumns.Account);
        var addressLookup = lmdbTx.OpenDatabase(AddressLookupTableName);
        var storage = lmdbTx.OpenDatabase(FlatDbColumns.Storage.ToString());

        var flatWriter = new BasePersistence.ToHashedWriteBatch<LMDBFlatWriter>(
            new LMDBFlatWriter(
                state,
                addressLookup,
                storage,
                lmdbTx
            )
        );

        var trieWriteBatch = new BaseTriePersistence.WriteBatch(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
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

                MarkWriteBatchComplete();
            })
        );

    }

    private readonly struct LMDBFlatWriter(
        IWriteBatch state,
        LightningDatabase addressLookup,
        LightningDatabase storage,
        LightningTransaction _lmdbTx
    ) : BasePersistence.IHashedFlatWriteBatch
    {
        // 20 byte
        private static byte[] nextAddressKey =
            Bytes.FromHexString("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        public void RemoveAccount(in ValueHash256 address)
        {
            state.Remove(EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], address));
        }

        public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value)
        {
            state.PutSpan(EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], address), value);
        }

        public uint GetNextAddress()
        {
            (MDBResultCode resultCode, MDBValue key, MDBValue value) = _lmdbTx.Get(addressLookup, nextAddressKey);
            uint existing = 0;
            if (resultCode == MDBResultCode.Success)
            {
                existing = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan());
            }

            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, existing + 1);
            resultCode = _lmdbTx.Put(addressLookup, nextAddressKey, buffer);
            if (resultCode != MDBResultCode.Success)
            {
                throw new InvalidOperationException($"Unable to increment address {resultCode}");
            }

            return existing + 1;
        }

        public MDBValue GetOrAllocateAddress(in ValueHash256 address)
        {
            (MDBResultCode resultCode, MDBValue key, MDBValue value) = _lmdbTx.Get(addressLookup, address.Bytes[..20]);
            if (resultCode == MDBResultCode.Success)
            {
                return value;
            }

            if (resultCode == MDBResultCode.NotFound)
            {
                uint nextAddr = GetNextAddress();
                Span<byte> buffer = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, nextAddr);
                resultCode = _lmdbTx.Put(addressLookup, address.Bytes[..20], buffer);
                if (resultCode != MDBResultCode.Success)
                {
                    throw new InvalidOperationException($"Unable to set address");
                }
            }

            (resultCode, key, value) = _lmdbTx.Get(addressLookup, address.Bytes[..20]);
            if (resultCode != MDBResultCode.Success)
            {
                throw new Exception("Unable to set address");
            }

            return value;
        }

        public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, in SlotValue? value)
        {
            MDBValue addr = GetOrAllocateAddress(address);
            ReadOnlySpan<byte> theKey = EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slotHash);
            if (value is null)
            {
                _lmdbTx.Delete(storage, theKey);
            }
            else
            {
                _lmdbTx.Put(storage, theKey, value.Value.AsSpan);
            }
        }

        public int SelfDestruct(in ValueHash256 accountPath)
        {
            Span<byte> firstKey = stackalloc byte[LMDBStoragePrefixLength]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[StorageKeyLength];

            // for storage the prefix might change depending on the encoding
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);

            MDBValue addr = GetOrAllocateAddress(accountPath);
            addr.AsSpan().CopyTo(firstKey);
            addr.AsSpan().CopyTo(lastKey);

            using var storageCursor = _lmdbTx.CreateCursor(storage);
            storageCursor.SetRange(firstKey);

            int removedEntry = 0;

            (MDBResultCode resultCode, MDBValue key, MDBValue value) = storageCursor.GetCurrent();
            while (true)
            {
                if (resultCode != MDBResultCode.Success) break;

                // Out of range
                int compare = Bytes.BytesComparer.Compare(key.AsSpan(), lastKey);
                if (compare >= 0) break;

                _lmdbTx.Delete(storage, key.AsSpan());

                (resultCode, key, value) = storageCursor.Next();
            }

            return removedEntry;
        }
    }

    private readonly struct LMDBFlatReader(
        IReadOnlyKeyValueStore state,
        LightningDatabase addressLookup,
        LightningDatabase storage,
        LightningTransaction lmdbTx
    ) : BasePersistence.IHashedFlatReader
    {
        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer)
        {
            ReadOnlySpan<byte> key = address.Bytes[..StateKeyPrefixLength];
            return state.GetSpanCopy(key, outBuffer);
        }

        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref SlotValue outValue)
        {
            (MDBResultCode resultCode, MDBValue key, MDBValue value) = lmdbTx.Get(addressLookup, address.Bytes[..20]);
            if (resultCode == MDBResultCode.NotFound)
            {
                return false;
            }

            if (resultCode != MDBResultCode.Success)
            {
                throw new Exception($"Unable to know address key. {resultCode}");
            }

            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = EncodeStorageKey(keySpan, value, slot);
            (resultCode, key, MDBValue valueMdb) = lmdbTx.Get(storage, storageKey);
            if (resultCode == MDBResultCode.NotFound) return false;
            if (resultCode != MDBResultCode.Success) throw new Exception($"Read storage raw failed with result code {resultCode}");
            valueMdb.AsSpan().CopyTo(outValue.AsSpan);
            return true;
        }
    }

    public IPersistenceWithConcurrentTrie.IWriteBatch CreateTrieWriteBatch(WriteFlags flags = WriteFlags.None)
    {
        var dbSnap = _db.CreateSnapshot();
        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        var trieWriteBatch = new BaseTriePersistence.WriteBatch(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
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

        public void SetTrieNodes(Hash256? address, in TreePath path, TrieNode tnValue)
        {
            trieWriteBatch.SetTrieNodes(address, path, tnValue);
        }
    }
}
