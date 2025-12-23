// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State.Flat.Persistence;

public class HashedFlatPersistence
{
    private const int StateKeyPrefixLength = 20;
    private const int StorageHashPrefixLength = 20; // Store prefix of the 32 byte of the storage. Reduces index size.
    private const int StorageSlotKeySize = 32;
    private const int StorageKeyLength = StorageHashPrefixLength + StorageSlotKeySize;
    private static ReadOnlySpan<byte> EncodeAccountKey(Span<byte> buffer, in Address addr, out ulong h1)
    {
        ValueHash256 hashBuffer = ValueKeccak.Zero;
        hashBuffer = addr.ToAccountPath;
        h1 = BinaryPrimitives.ReadUInt64LittleEndian(hashBuffer.Bytes);
        hashBuffer.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        return buffer[..StateKeyPrefixLength];
    }

    internal static ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, in Address addr, in UInt256 slot, out ulong h1)
    {
        ValueHash256 hashBuffer = ValueKeccak.Zero;
        hashBuffer = addr.ToAccountPath; // 75ns on average
        hashBuffer.Bytes[..StorageHashPrefixLength].CopyTo(buffer);

        // around 300ns on average. 30% keccak cache hit rate.
        StorageTree.ComputeKeyWithLookup(slot, buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);

        h1 = Mix(BinaryPrimitives.ReadUInt64LittleEndian(buffer), BinaryPrimitives.ReadUInt64LittleEndian(buffer[StorageHashPrefixLength..]));
        return buffer[..StorageKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeStorageKeyHashed(Span<byte> buffer, in ValueHash256 addrHash, in ValueHash256 slotHash, out ulong h1)
    {
        addrHash.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        slotHash.Bytes.CopyTo(buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);
        h1 = Mix(BinaryPrimitives.ReadUInt64LittleEndian(addrHash.Bytes), BinaryPrimitives.ReadUInt64LittleEndian(buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]));
        return buffer[..StorageKeyLength];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Mix(ulong a, ulong b)
    {
        return (a ^ RotateLeft(b, 23)) * 0x9E3779B97F4A7C15UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong RotateLeft(ulong x, int k)
        => (x << k) | (x >> (64 - k));

    public class WriteBatch(
        ISortedKeyValueStore storageSnap,
        IWriteOnlyKeyValueStore state,
        IWriteOnlyKeyValueStore storage,
        WriteFlags flags,
        SegmentedBloom bloomFilter
    )
    {
        internal AccountDecoder _accountDecoder = AccountDecoder.Instance;

        public int SelfDestruct(Address addr)
        {
            ValueHash256 accountPath = addr.ToAccountPath;
            Span<byte> firstKey = stackalloc byte[StorageHashPrefixLength]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[StorageKeyLength];
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(firstKey);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(lastKey);

            int removedEntry = 0;
            // for storage the prefix might change depending on the encoding
            EncodeAccountKey(firstKey, addr, out _);
            EncodeAccountKey(lastKey, addr, out _);
            using (ISortedView storageReader = storageSnap.GetViewBetween(firstKey, lastKey))
            {
                IWriteOnlyKeyValueStore? storageWriter = storage;
                while (storageReader.MoveNext())
                {
                    storageWriter.Remove(storageReader.CurrentKey);
                    removedEntry++;
                }
            }

            return removedEntry;
        }

        public void RemoveAccount(Address addr)
        {
            state.Remove(EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr, out _));
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            ReadOnlySpan<byte> key = EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr, out var bloomHash);

            if (account != null)
            {
                bloomFilter.Add(bloomHash);
            }

            state.PutSpan(key, stream.AsSpan());
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ReadOnlySpan<byte> theKey =  EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot, out ulong bloomHash);

            bloomFilter.Add(bloomHash);
            storage.PutSpan(theKey, value, flags);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot, out _);
            storage.Remove(theKey);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash.ValueHash256, slotHash.ValueHash256, out ulong bloomHash);
            bloomFilter.Add(bloomHash);
            storage.PutSpan(theKey, value, flags);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            var key = addrHash.Bytes[..StateKeyPrefixLength];
            bloomFilter.Add(BinaryPrimitives.ReadUInt64LittleEndian(key));
            state.PutSpan(key, stream.AsSpan(), flags);
        }
    }


    public class Reader(
        IReadOnlyKeyValueStore _state,
        IReadOnlyKeyValueStore _storage,
        SegmentedBloom _bloomFilter
    )
    {
        internal AccountDecoder _accountDecoder = AccountDecoder.Instance;

        private static Counter _slotBloomHit = Metrics.CreateCounter("rocksdb_slot_bloom", "slot_blom", "hitmiss");
        private static Counter.Child _slotBloomHitHit = _slotBloomHit.WithLabels("true_positive");
        private static Counter.Child _slotBloomHitMiss = _slotBloomHit.WithLabels("false_positive");


        public bool TryGetAccount(Address address, out Account? acc)
        {
            var key = EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], address, out ulong bloomHash);
            if (!_bloomFilter.MightContain(bloomHash))
            {
                acc = null;
                return true;
            }

            Span<byte> value = _state.GetSpan(key);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    acc = null;
                    return true;
                }

                var ctx = new Rlp.ValueDecoderContext(value);
                acc = _accountDecoder.Decode(ref ctx);
                return true;
            }
            finally
            {
                _state.DangerousReleaseMemory(value);
            }
        }


        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKey(stackalloc byte[StorageKeyLength], address, index, out ulong h1);
            if (!_bloomFilter.MightContain(h1))
            {
                valueBytes = null;
                return true;
            }

            Span<byte> value = _storage.GetSpan(theKey);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    _slotBloomHitMiss.Inc();
                    valueBytes = null;
                    return true;
                }

                _slotBloomHitHit.Inc();
                valueBytes = value.ToArray();
                return true;
            }
            finally
            {
                _storage.DangerousReleaseMemory(value);
            }
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            return GetAccountRaw(addrHash.ValueHash256);
        }

        private byte[]? GetAccountRaw(in ValueHash256 accountHash)
        {
            return _state.GetSpan(accountHash.Bytes[..StateKeyPrefixLength]).ToArray();
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = EncodeStorageKeyHashed(keySpan, addrHash.ValueHash256, slotHash.ValueHash256, out ulong _);
            return _storage.Get(storageKey);
        }
    }
}
