// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.MclBindings;

namespace Nethermind.Evm.Precompiles;

internal static unsafe partial class BN254
{
    private const int G2Size = 128;
    private const int G2CacheCount = 4096;
    private const uint G2CacheIndexMask = G2CacheCount - 1;
    private const uint CacheWriteMarker = 1;
    private const uint CacheVersionIncrement = 2;
    private const int CacheLineSize = 64;
    private const int PairCacheCount = 1024;
    private const uint PairCacheIndexMask = PairCacheCount - 1;
    private const int PairSeenCount = 4096;
    private const uint PairSeenIndexMask = PairSeenCount - 1;
    private const byte PairScoreLimit = 15;

    private static readonly nuint G2CacheStride = (nuint)(sizeof(mclBnG2) + sizeof(G2CacheEntry) + sizeof(ulong) - 1) & ~(nuint)(sizeof(ulong) - 1);
    private static readonly byte* G2Cache = AllocateCache(G2CacheCount * G2CacheStride);
    private static readonly nuint PairCacheStride = (nuint)(sizeof(mclBnGT) + sizeof(PairCacheEntry) + sizeof(ulong) - 1) & ~(nuint)(sizeof(ulong) - 1);
    private static readonly byte* PairCache = AllocateCache(PairCacheCount * PairCacheStride);
    private static readonly uint* PairSeen = (uint*)AllocateCache(PairSeenCount * sizeof(uint));
    private static readonly byte* PairScore = AllocateCache(PairCacheCount);

    private static byte* AllocateCache(nuint size)
    {
        byte* cache = (byte*)NativeMemory.AlignedAlloc(size, CacheLineSize);
        NativeMemory.Clear(cache, size);
        GC.AddMemoryPressure((long)size);
        return cache;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* GetG2CacheSlot(byte* data) =>
        G2Cache + ((uint)new ReadOnlySpan<byte>(data, G2Size).FastHash() & G2CacheIndexMask) * G2CacheStride;

    static partial void LoadValidatedG2(byte* data, ref mclBnG2 point, ref bool cached)
    {
        byte* slot = GetG2CacheSlot(data);
        G2CacheEntry* entry = (G2CacheEntry*)(slot + sizeof(mclBnG2));
        uint version = Volatile.Read(ref entry->Version);
        if (version == 0 || (version & CacheWriteMarker) != 0)
            return;

        // Safety: the copy is used only after the full fence and a matching second version read, a failed copy is discarded, and the native table lives for the process lifetime.
        point = Unsafe.AsRef<mclBnG2>(slot);
        bool matches = new ReadOnlySpan<byte>(entry->Key, G2Size).SequenceEqual(new ReadOnlySpan<byte>(data, G2Size));

        Interlocked.MemoryBarrier();

        if (matches && Volatile.Read(ref entry->Version) == version)
        {
            cached = true;
            return;
        }

        point = default;
    }

    static partial void StoreValidatedG2(byte* data, in mclBnG2 point)
    {
        byte* slot = GetG2CacheSlot(data);
        G2CacheEntry* entry = (G2CacheEntry*)(slot + sizeof(mclBnG2));
        uint version = Volatile.Read(ref entry->Version);
        if ((version & CacheWriteMarker) != 0 ||
            Interlocked.CompareExchange(ref entry->Version, version | CacheWriteMarker, version) != version)
            return;

        Unsafe.AsRef<mclBnG2>(slot) = point;
        new ReadOnlySpan<byte>(data, G2Size).CopyTo(new Span<byte>(entry->Key, G2Size));
        Volatile.Write(ref entry->Version, version + CacheVersionIncrement);
    }

    static partial void LoadPairMillerLoop(byte* pair, ref mclBnGT value, ref bool cached, ref bool repeated)
    {
        uint hash = (uint)new ReadOnlySpan<byte>(pair, PairSize).FastHash();
        uint index = hash & PairCacheIndexMask;
        byte* slot = PairCache + index * PairCacheStride;
        PairCacheEntry* entry = (PairCacheEntry*)(slot + sizeof(mclBnGT));
        byte* score = PairScore + index;
        uint version = Volatile.Read(ref entry->Version);
        if (version != 0 && (version & CacheWriteMarker) == 0)
        {
            // Safety: the copy is used only after the full fence and a matching second version read, a failed copy is discarded, and the native table lives for the process lifetime.
            value = Unsafe.AsRef<mclBnGT>(slot);
            bool matches = new ReadOnlySpan<byte>(entry->Key, PairSize).SequenceEqual(new ReadOnlySpan<byte>(pair, PairSize));

            Interlocked.MemoryBarrier();

            if (matches && Volatile.Read(ref entry->Version) == version)
            {
                if (*score != PairScoreLimit)
                    *score = PairScoreLimit;

                cached = true;
                return;
            }
        }

        // PairSeen and PairScore are approximate admission data: a race changes only what is cached, never validation or the result.
        uint tag = hash | 1;
        uint* seen = PairSeen + (hash & PairSeenIndexMask);
        if (*seen != tag)
        {
            *seen = tag;
            return;
        }

        byte current = *score;
        if (version != 0 && current != 0)
        {
            *score = (byte)(current - 1);
            return;
        }

        repeated = true;
    }

    static partial void StorePairMillerLoop(byte* pair, in mclBnGT value)
    {
        uint hash = (uint)new ReadOnlySpan<byte>(pair, PairSize).FastHash();
        uint index = hash & PairCacheIndexMask;
        byte* slot = PairCache + index * PairCacheStride;
        PairCacheEntry* entry = (PairCacheEntry*)(slot + sizeof(mclBnGT));
        uint version = Volatile.Read(ref entry->Version);
        if ((version & CacheWriteMarker) != 0 ||
            Interlocked.CompareExchange(ref entry->Version, version | CacheWriteMarker, version) != version)
            return;

        Unsafe.AsRef<mclBnGT>(slot) = value;
        new ReadOnlySpan<byte>(pair, PairSize).CopyTo(new Span<byte>(entry->Key, PairSize));
        Volatile.Write(ref entry->Version, version + CacheVersionIncrement);
        PairScore[index] = PairScoreLimit;
    }

    private struct G2CacheEntry
    {
        public uint Version;
        public fixed byte Key[G2Size];
    }

    private struct PairCacheEntry
    {
        public uint Version;
        public fixed byte Key[PairSize];
    }
}
