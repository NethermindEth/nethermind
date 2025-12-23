// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// 30%-40% of the slot reads are empty slot. These can be filtered relatively easily via a large (around 2-3GB bloom filter),
/// Rocksdb can also be configured to have bloom filter all the way to the last level, but its per sst file, and its not as fast
/// as this implementation. That said, it is already pretty good that on most block, the potential gain for this implementation is pretty small.
/// Unfortunately this comes at a cost of heavy write amplification and the memory for the bloom. The heavy write amplification
/// seems to slowdown other part of the code, hence it is unclear if it is always faster, but on some range of block,
/// it can be up to 20% almost free mgas/sec.
/// </summary>
public static class BloomFlatWrapper
{
    public readonly struct BloomWriter<TInnerWriteBatch>(
        TInnerWriteBatch innerWriteBatch,
        SegmentedBloom bloomFilter
    ) : BaseRocksdbPersistence.IHashedFlatWriteBatch
        where TInnerWriteBatch: struct, BaseRocksdbPersistence.IHashedFlatWriteBatch
    {
        public int SelfDestruct(in ValueHash256 address)
        {
            return innerWriteBatch.SelfDestruct(in address);
        }

        public void RemoveAccount(in ValueHash256 address)
        {
            innerWriteBatch.RemoveAccount(in address);
        }

        public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value)
        {
            bloomFilter.Add(BinaryPrimitives.ReadUInt64LittleEndian(address.BytesAsSpan));
            innerWriteBatch.SetAccount(in address, value);
        }

        public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, ReadOnlySpan<byte> value)
        {
            bloomFilter.Add(Mix(BinaryPrimitives.ReadUInt64LittleEndian(address.Bytes), BinaryPrimitives.ReadUInt64LittleEndian(slotHash.Bytes)));
            innerWriteBatch.SetStorage(in address, in slotHash, value);
        }

        public void RemoveStorage(in ValueHash256 address, in ValueHash256 slotHash)
        {
            innerWriteBatch.RemoveStorage(in address, in slotHash);
        }
    }

    public readonly struct BloomInterceptor<TFlatReader>(
        TFlatReader baseFlatReader,
        SegmentedBloom bloomFilter
    )
        : BaseRocksdbPersistence.IHashedFlatReader
        where TFlatReader: struct, BaseRocksdbPersistence.IHashedFlatReader
    {
        public int GetAccount(in ValueHash256 address, Span<byte> outBuffer)
        {
            if (!bloomFilter.MightContain(BinaryPrimitives.ReadUInt64LittleEndian(address.BytesAsSpan)))
            {
                return 0;
            }

            return baseFlatReader.GetAccount(in address, outBuffer);
        }

        public int GetStorage(in ValueHash256 address, in ValueHash256 slotHash, Span<byte> outBuffer)
        {
            if (!bloomFilter.MightContain(Mix(BinaryPrimitives.ReadUInt64LittleEndian(address.Bytes), BinaryPrimitives.ReadUInt64LittleEndian(slotHash.Bytes))))
            {
                return 0;
            }

            return baseFlatReader.GetStorage(in address, in slotHash, outBuffer);
        }
    }

    static ulong Mix(ulong a, ulong b)
    {
        return (a ^ RotateLeft(b, 23)) * 0x9E3779B97F4A7C15UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong RotateLeft(ulong x, int k)
        => (x << k) | (x >> (64 - k));
}
