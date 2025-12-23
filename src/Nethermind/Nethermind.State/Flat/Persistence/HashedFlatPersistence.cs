// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    private static ReadOnlySpan<byte> EncodeAccountKeyHashed(Span<byte> buffer, in ValueHash256 address, out ulong h1)
    {
        h1 = BinaryPrimitives.ReadUInt64LittleEndian(address.Bytes);
        address.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        return buffer[..StateKeyPrefixLength];
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

    public struct WriteBatch(
        ISortedKeyValueStore storageSnap,
        IWriteOnlyKeyValueStore state,
        IWriteOnlyKeyValueStore storage,
        WriteFlags flags,
        SegmentedBloom bloomFilter
    ) : BaseRocksdbPersistence.IHashedFlatWriteBatch
    {
        public int SelfDestruct(in ValueHash256 accountPath)
        {
            Span<byte> firstKey = stackalloc byte[StorageHashPrefixLength]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[StorageKeyLength];
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(firstKey);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(lastKey);

            int removedEntry = 0;
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

        public void RemoveAccount(in ValueHash256 addrHash)
        {
            ReadOnlySpan<byte> key = addrHash.Bytes[..StateKeyPrefixLength];
            state.Remove(key);
        }

        public void RemoveStorage(in ValueHash256 addrHash, in ValueHash256 slotHash)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash, slotHash, out ulong bloomHash);
            storage.Remove(theKey);
        }

        public void SetStorage(in ValueHash256 addrHash, in ValueHash256 slotHash, ReadOnlySpan<byte> value)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash, slotHash, out ulong bloomHash);
            bloomFilter.Add(bloomHash);
            storage.PutSpan(theKey, value, flags);
        }

        public void SetAccount(in ValueHash256 addrHash, ReadOnlySpan<byte> account)
        {
            ReadOnlySpan<byte> key = addrHash.Bytes[..StateKeyPrefixLength];
            bloomFilter.Add(BinaryPrimitives.ReadUInt64LittleEndian(key));
            state.PutSpan(key, account, flags);
        }
    }


    public struct Reader(
        IReadOnlyKeyValueStore _state,
        IReadOnlyKeyValueStore _storage,
        SegmentedBloom _bloomFilter
    ) : BaseRocksdbPersistence.IHashedFlatReader
    {
        private static Counter _slotBloomHit = Metrics.CreateCounter("rocksdb_slot_bloom", "slot_blom", "hitmiss");
        private static Counter.Child _slotBloomHitHit = _slotBloomHit.WithLabels("true_positive");
        private static Counter.Child _slotBloomHitMiss = _slotBloomHit.WithLabels("false_positive");

        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer)
        {
            ReadOnlySpan<byte> key = EncodeAccountKeyHashed(stackalloc byte[StateKeyPrefixLength], address, out ulong bloomHash);
            if (!_bloomFilter.MightContain(bloomHash))
            {
                return 0;
            }

            ReadOnlySpan<byte> span = _state.GetSpan(key);
            try
            {
                span.CopyTo(outBuffer);
                return span.Length;
            }
            finally
            {
                _state.DangerousReleaseMemory(span);
            }
        }

        public int GetStorage(in ValueHash256 address, in ValueHash256 slot, Span<byte> outBuffer)
        {
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = EncodeStorageKeyHashed(keySpan, address, slot, out ulong bloomHash);
            if (!_bloomFilter.MightContain(bloomHash))
            {
                return 0;
            }

            Span<byte> value = _storage.GetSpan(storageKey);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    _slotBloomHitMiss.Inc();
                    return 0;
                }

                _slotBloomHitHit.Inc();
                value.CopyTo(outBuffer);
                return value.Length;
            }
            finally
            {
                _storage.DangerousReleaseMemory(value);
            }
        }
    }
}
