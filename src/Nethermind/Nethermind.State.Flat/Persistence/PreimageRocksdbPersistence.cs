// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence.BloomFilter;

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

    private readonly SegmentedBloom _bloomFilter;
    private IDb _preimageDb;

    public PreimageRocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        [KeyFilter(DbNames.Preimage)] IDb preimageDb,
        [KeyFilter(DbNames.Flat)] SegmentedBloom bloomFilter)
    {
        _db = db;
        _preimageDb = preimageDb;
        _bloomFilter = bloomFilter;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Mix(ulong a, ulong b)
    {
        return (a ^ RotateLeft(b, 23)) * 0x9E3779B97F4A7C15UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong RotateLeft(ulong x, int k)
        => (x << k) | (x >> (64 - k));

    public IPersistence.IPersistenceReader CreateReader()
    {
        var snapshot = _db.CreateSnapshot();
        var trieReader = new BaseTriePersistence.Reader(
            snapshot.GetColumn(FlatDbColumns.StateTopNodes),
            snapshot.GetColumn(FlatDbColumns.StateNodes),
            snapshot.GetColumn(FlatDbColumns.StorageNodes)
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
            batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
            batch.GetColumnBatch(FlatDbColumns.StateNodes),
            batch.GetColumnBatch(FlatDbColumns.StorageNodes),
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
                    _bloomFilter.Flush();
                }
            })
        );
    }

    public struct FakeHashWriter<TWriteBatch>(
        TWriteBatch flatWriteBatch,
        IWriteBatch preimageWriteBatch,
        IKeyValueStore preimageDb
    ) : BasePersistence.IFlatWriteBatch
        where TWriteBatch : struct, BasePersistence.IHashedFlatWriteBatch
    {
        internal AccountDecoder _accountDecoder = AccountDecoder.Instance;
        private TWriteBatch _flatWriteBatch = flatWriteBatch;

        public int SelfDestruct(Address addr)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 computed = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], addr.Bytes);

            return _flatWriteBatch.SelfDestruct(fakeAddrHash);
        }

        public void RemoveAccount(Address addr)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 computed = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], addr.Bytes);

            _flatWriteBatch.RemoveAccount(fakeAddrHash);
        }

        public void SetAccount(Address addr, Account account)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 computed = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], addr.Bytes);

            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            _flatWriteBatch.SetAccount(fakeAddrHash, stream.AsSpan());
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 fakeSlotHash = ValueKeccak.Zero;
            slot.ToBigEndian(fakeSlotHash.BytesAsSpan);

            ValueHash256 computed = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], addr.Bytes);

            StorageTree.ComputeKeyWithLookup(slot,  computed.BytesAsSpan);
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], slot.ToBigEndian());

            _flatWriteBatch.SetStorage(fakeAddrHash, fakeSlotHash, value);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ValueHash256 fakeAddrHash = ValueKeccak.Zero;
            addr.Bytes.CopyTo(fakeAddrHash.BytesAsSpan);

            ValueHash256 fakeSlotHash = ValueKeccak.Zero;
            slot.ToBigEndian(fakeSlotHash.BytesAsSpan);

            ValueHash256 computed = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], addr.Bytes);

            StorageTree.ComputeKeyWithLookup(slot,  computed.BytesAsSpan);
            preimageWriteBatch.PutSpan(computed.BytesAsSpan[..PreimageLookupSize], slot.ToBigEndian());

            _flatWriteBatch.RemoveStorage(fakeAddrHash, fakeSlotHash);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            try
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
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error {e}");
                throw;
            }
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            try
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
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error {e}");
                throw;
            }
        }
    }

    public struct FakeHashFlatReader<TFlatReader>(
        TFlatReader flatReader
    ) : BasePersistence.IFlatReader
        where TFlatReader : struct, BasePersistence.IHashedFlatReader
    {
        internal AccountDecoder _accountDecoder = AccountDecoder.Instance;
        private int _accountSpanBufferSize = 256;
        private int _slotSpanBufferSize = 40;

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

            Span<byte> valueBuffer = stackalloc byte[_slotSpanBufferSize];
            int responseSize = flatReader.GetStorage(fakeHash, fakeSlotHash, valueBuffer);
            if (responseSize == 0)
            {
                return false;
            }

            int offset = SlotValue.ByteCount - responseSize;
            valueBuffer[..responseSize].CopyTo(outValue.AsSpan[offset..]);
            return true;
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            throw new InvalidOperationException("Raw operation not available in preimage mode");
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            throw new InvalidOperationException("Raw operation not available in preimage mode");
        }
    }
}
