// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.MclBindings;

namespace Nethermind.Evm.Precompiles;

internal static unsafe partial class BN254
{
    private const int G2Size = 128;
    private const int G2CacheCount = 4096;
    private const uint G2CacheIndexMask = G2CacheCount - 1;
    private const uint G2CacheWriteMarker = 1;
    private const uint G2CacheVersionIncrement = 2;
    private const int CacheLineSize = 64;

    private static readonly nuint G2CacheStride = (nuint)(sizeof(mclBnG2) + sizeof(G2CacheEntry) + sizeof(ulong) - 1) & ~(nuint)(sizeof(ulong) - 1);
    private static readonly byte* G2Cache = AllocateG2Cache();

    private static byte* AllocateG2Cache()
    {
        nuint size = G2CacheCount * G2CacheStride;
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
        if (version == 0 || (version & G2CacheWriteMarker) != 0)
            return;

        point = Unsafe.AsRef<mclBnG2>(slot);
        bool matches = new ReadOnlySpan<byte>(entry->Key, G2Size).SequenceEqual(new ReadOnlySpan<byte>(data, G2Size));

        if (!Sse.IsSupported)
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
        if ((version & G2CacheWriteMarker) != 0 ||
            Interlocked.CompareExchange(ref entry->Version, version | G2CacheWriteMarker, version) != version)
            return;

        Unsafe.AsRef<mclBnG2>(slot) = point;
        new ReadOnlySpan<byte>(data, G2Size).CopyTo(new Span<byte>(entry->Key, G2Size));
        Volatile.Write(ref entry->Version, version + G2CacheVersionIncrement);
    }

    private struct G2CacheEntry
    {
        public uint Version;
        public fixed byte Key[G2Size];
    }
}
