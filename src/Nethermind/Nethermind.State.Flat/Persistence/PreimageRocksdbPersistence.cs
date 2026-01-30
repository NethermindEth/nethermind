// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Preimage means that instead of hashing the address and slot and using the address as a key, it uses the address and
/// slot directly as a key. This implementation simply fakes the hash by copying the bytes directly.
/// This has some benefits:
/// - Skipping hash calculation, address and slot (around 0.3 micros).
/// - Improved compression ratio, lower storage db size by about 15%, and therefore better os cache utilization.
/// - Related slot values tend to be closer together, resulting in a better block cache.
/// However, it has some major downsides.
/// - Cannot snap sync.
/// - Cannot import without a complete preimage db.
/// </summary>
public class PreimageRocksdbPersistence(IColumnsDb<FlatDbColumns> db) : IPersistence
{
    private static readonly byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    public void Flush() => db.Flush();

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(CurrentStateKey);
        if (bytes is null || bytes.Length == 0)
        {
            return new StateId(-1, Keccak.EmptyTreeHash);
        }

        long blockNumber = BinaryPrimitives.ReadInt64BigEndian(bytes);
        ValueHash256 stateHash = new(bytes[8..]);
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
        IColumnDbSnapshot<FlatDbColumns> snapshot = db.CreateSnapshot();
        BaseTriePersistence.Reader trieReader = new(
            snapshot.GetColumn(FlatDbColumns.StateTopNodes),
            snapshot.GetColumn(FlatDbColumns.StateNodes),
            snapshot.GetColumn(FlatDbColumns.StorageNodes),
            snapshot.GetColumn(FlatDbColumns.FallbackNodes)
        );

        StateId currentState = ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

        ISortedKeyValueStore state = (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Account);
        ISortedKeyValueStore storage = (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Storage);

        FakeHashFlatReader<BaseFlatPersistence.Reader> flatReader = new(
            new BaseFlatPersistence.Reader(
                state,
                storage,
                isPreimageMode: true
            )
        );

        return new BasePersistence.Reader<FakeHashFlatReader<BaseFlatPersistence.Reader>, BaseTriePersistence.Reader>(
            flatReader,
            trieReader,
            currentState,
            new Reactive.AnonymousDisposable(() =>
            {
                snapshot.Dispose();
            })
        );
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();
        IColumnDbSnapshot<FlatDbColumns> dbSnap = db.CreateSnapshot();
        StateId currentState = ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (from != StateId.Sync && currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        FakeHashWriter<BaseFlatPersistence.WriteBatch> flatWriter = new(
            new BaseFlatPersistence.WriteBatch(
                (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Account),
                (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
                batch.GetColumnBatch(FlatDbColumns.Account),
                batch.GetColumnBatch(FlatDbColumns.Storage),
                flags
            )
        );

        BaseTriePersistence.WriteBatch trieWriteBatch = new BaseTriePersistence.WriteBatch(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
            batch.GetColumnBatch(FlatDbColumns.FallbackNodes),
            flags);

        StateId toCopy = to;
        return new BasePersistence.WriteBatch<FakeHashWriter<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(
            flatWriter,
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                if (toCopy != StateId.Sync)
                    SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), toCopy);
                batch.Dispose();
                dbSnap.Dispose();
                if (!flags.HasFlag(WriteFlags.DisableWAL))
                {
                    db.Flush(onlyWal: true);
                }
            })
        );
    }

    private struct FakeHashWriter<TWriteBatch>(
        TWriteBatch flatWriteBatch
    ) : BasePersistence.IFlatWriteBatch
        where TWriteBatch : struct, BasePersistence.IHashedFlatWriteBatch
    {
        private TWriteBatch _flatWriteBatch = flatWriteBatch;

        public void Clear() => _flatWriteBatch.Clear();

        public void SelfDestruct(Address addr)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);
            _flatWriteBatch.SelfDestruct(fakeAddrHash);
        }

        public void SetAccount(Address addr, Account? account)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            if (account is null)
            {
                _flatWriteBatch.RemoveAccount(fakeAddrHash);
                return;
            }

            using NettyRlpStream stream = AccountDecoder.Slim.EncodeToNewNettyStream(account);
            _flatWriteBatch.SetAccount(fakeAddrHash, stream.AsSpan());
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 fakeSlotHash = ValueKeccak.Zero;
            slot.ToBigEndian(fakeSlotHash.BytesAsSpan);

            _flatWriteBatch.SetStorage(fakeAddrHash, fakeSlotHash, value);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value) =>
            throw new InvalidOperationException("Raw operations not available in preimage mode");

        public void SetAccountRaw(Hash256 addrHash, Account account) =>
            throw new InvalidOperationException("Raw operations not available in preimage mode");

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            throw new InvalidOperationException("Range deletion not available in preimage mode");

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
            throw new InvalidOperationException("Range deletion not available in preimage mode");
    }

    public struct FakeHashFlatReader<TFlatReader>(
        TFlatReader flatReader
    ) : BasePersistence.IFlatReader
        where TFlatReader : struct, BasePersistence.IHashedFlatReader
    {
        private const int AccountSpanBufferSize = 256;
        private TFlatReader _flatReader = flatReader;

        public Account? GetAccount(Address address)
        {
            ValueHash256 fakeHash = ValueKeccak.Zero;
            address.Bytes.CopyTo(fakeHash.BytesAsSpan);

            Span<byte> valueBuffer = stackalloc byte[AccountSpanBufferSize];
            int responseSize = _flatReader.GetAccount(fakeHash, valueBuffer);
            if (responseSize == 0)
            {
                return null;
            }

            Rlp.ValueDecoderContext ctx = new(valueBuffer[..responseSize]);
            return AccountDecoder.Slim.Decode(ref ctx);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            ValueHash256 fakeHash = ValueKeccak.Zero;
            address.Bytes.CopyTo(fakeHash.BytesAsSpan);

            ValueHash256 fakeSlotHash = ValueKeccak.Zero;
            slot.ToBigEndian(fakeSlotHash.BytesAsSpan);

            return TryGetSlotRaw(fakeHash, fakeSlotHash, ref outValue);
        }

        public byte[] GetAccountRaw(Hash256 addrHash) =>
            throw new InvalidOperationException("Raw operation not available in preimage mode");

        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue) =>
            _flatReader.TryGetStorage(address, slotHash, ref outValue);

        public IPersistence.IFlatIterator CreateAccountIterator() =>
            _flatReader.CreateAccountIterator();

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey) =>
            _flatReader.CreateStorageIterator(accountKey);

        public bool IsPreimageMode => _flatReader.IsPreimageMode;
    }
}
