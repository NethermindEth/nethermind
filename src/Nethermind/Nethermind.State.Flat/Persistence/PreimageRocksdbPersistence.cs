// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Preimage means that instead of hashing the address and slot and using the address as key, it uses the address and
/// slot directly as key. This implementation simply fake the hash by copying the bytes directly.
/// This has a few benefit:
/// - Skipping hash calculation, address and slot (around 0.3 micros).
/// - Improved compression ratio, lower storage db size by about 15%, and therefore better os cache utilization.
/// - Related slot value tend to be closer together resulting in better block cache.
/// However, it has some major downside.
/// - Cannot snap sync.
/// - Cannot import without a complete preimage db.
/// </summary>
public class PreimageRocksdbPersistence : IPersistence
{
    private const int PreimageLookupSize = 12; // Store only 12 byte

    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    private IDb _preimageDb;

    public PreimageRocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        [KeyFilter(DbNames.Preimage)] IDb preimageDb)
    {
        _db = db;
        _preimageDb = preimageDb;
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
        var trieReader = new BaseTriePersistence.Reader(
            snapshot.GetColumn(FlatDbColumns.StateTopNodes),
            snapshot.GetColumn(FlatDbColumns.StateNodes),
            snapshot.GetColumn(FlatDbColumns.StorageNodes),
            snapshot.GetColumn(FlatDbColumns.FallbackNodes)
        );

        var currentState = ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));

        IReadOnlyKeyValueStore state = snapshot.GetColumn(FlatDbColumns.Account);
        IReadOnlyKeyValueStore storage = snapshot.GetColumn(FlatDbColumns.Storage);

        var flatReader = new FakeHashFlatReader<BaseFlatPersistence.Reader>(
            new BaseFlatPersistence.Reader(
                state,
                storage
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

    public IPersistence.IWriteBatch CreateWriteBatch(StateId from, StateId to, WriteFlags flags)
    {
        IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        IWriteBatch preimageWriteBatch = _preimageDb.StartWriteBatch();
        IColumnDbSnapshot<FlatDbColumns> dbSnap = _db.CreateSnapshot();
        StateId currentState = ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        var flatWriter = new FakeHashWriter<BaseFlatPersistence.WriteBatch>(
            new BaseFlatPersistence.WriteBatch(
                ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage)),
                batch.GetColumnBatch(FlatDbColumns.Account),
                batch.GetColumnBatch(FlatDbColumns.Storage),
                flags
            ),
            preimageWriteBatch,
            _preimageDb
        );

        var trieWriteBatch = new BaseTriePersistence.WriteBatch(
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
            (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
            batch.GetColumnBatch(FlatDbColumns.FallbackNodes),
            flags);

        return new BasePersistence.WriteBatch<FakeHashWriter<BaseFlatPersistence.WriteBatch>, BaseTriePersistence.WriteBatch>(
            flatWriter,
            trieWriteBatch,
            new Reactive.AnonymousDisposable(() =>
            {
                SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to);
                batch.Dispose();
                preimageWriteBatch.Dispose();
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
        return true;
    }

    public struct FakeHashWriter<TWriteBatch>(
        TWriteBatch flatWriteBatch,
        IWriteBatch preimageWriteBatch,
        IKeyValueStore preimageDb
    ) : BasePersistence.IFlatWriteBatch
        where TWriteBatch : struct, BasePersistence.IHashedFlatWriteBatch
    {
        internal AccountDecoder _accountDecoder = AccountDecoder.Slim;
        private TWriteBatch _flatWriteBatch = flatWriteBatch;

        public int SelfDestruct(Address addr)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 computed = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], addr.Bytes);

            return _flatWriteBatch.SelfDestruct(fakeAddrHash);
        }

        public void SetAccount(Address addr, Account? account)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 computed = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], addr.Bytes);

            if (account is null)
            {
                _flatWriteBatch.RemoveAccount(fakeAddrHash);
                return;
            }

            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            _flatWriteBatch.SetAccount(fakeAddrHash, stream.AsSpan());
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 fakeSlotHash = ValueKeccak.Zero;
            slot.ToBigEndian(fakeSlotHash.BytesAsSpan);

            ValueHash256 computed = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], addr.Bytes);

            StorageTree.ComputeKeyWithLookup(slot,  ref computed);
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], slot.ToBigEndian());

            _flatWriteBatch.SetStorage(fakeAddrHash, fakeSlotHash, value);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value)
        {
            byte[]? addressBytes = preimageDb.Get(addrHash.Bytes);
            if (addressBytes == null || addressBytes.Length != 20)
            {
                throw new InvalidOperationException(
                    $"Unable to translate back hash {addrHash} to address. Got {addressBytes?.ToHexString()}");
            }

            Address addr = new Address(addressBytes);

            byte[]? slotBytes = preimageDb.Get(slotHash.Bytes);
            if (slotBytes == null || slotBytes.Length != 32)
            {
                throw new InvalidOperationException(
                    $"Unable to translate back slot {slotHash} to slot. Got {slotBytes?.ToHexString()}");
            }

            UInt256 slot = new UInt256(slotBytes, isBigEndian: true);
            SetStorage(addr, slot, value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            byte[]? addressBytes = preimageDb.Get(addrHash.Bytes);
            if (addressBytes == null || addressBytes.Length != 20)
            {
                throw new InvalidOperationException( $"Unable to translate back hash {addrHash} to address. Got {addressBytes?.ToHexString()}");
            }

            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            _flatWriteBatch.SetAccount(addrHash, stream.AsSpan());

            Address addr = new Address(addressBytes);
            SetAccount(addr, account);
        }
    }

    public struct FakeHashFlatReader<TFlatReader>(
        TFlatReader flatReader
    ) : BasePersistence.IFlatReader
        where TFlatReader : struct, BasePersistence.IHashedFlatReader
    {
        internal AccountDecoder _accountDecoder = AccountDecoder.Slim;
        private int _accountSpanBufferSize = 256;

        public Account? GetAccount(Address address)
        {
            ValueHash256 fakeHash = ValueKeccak.Zero;
            address.Bytes.CopyTo(fakeHash.BytesAsSpan);

            Span<byte> valueBuffer = stackalloc byte[_accountSpanBufferSize];
            int responseSize = flatReader.GetAccount(fakeHash, valueBuffer);
            if (responseSize == 0)
            {
                return null;
            }

            var ctx = new Rlp.ValueDecoderContext(valueBuffer[..responseSize]);
            return _accountDecoder.Decode(ref ctx);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            ValueHash256 fakeHash = ValueKeccak.Zero;
            address.Bytes.CopyTo(fakeHash.BytesAsSpan);

            ValueHash256 fakeSlotHash = ValueKeccak.Zero;
            slot.ToBigEndian(fakeSlotHash.BytesAsSpan);

            return TryGetSlotRaw(fakeHash, fakeSlotHash, ref outValue);
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            throw new InvalidOperationException("Raw operation not available in preimage mode");
        }

        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue)
        {
            return flatReader.TryGetStorage(address, slotHash, ref outValue);
        }
    }
}
