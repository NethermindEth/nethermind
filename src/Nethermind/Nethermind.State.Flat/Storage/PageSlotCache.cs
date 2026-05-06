// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Receives eviction notifications from <see cref="PageSlotCache"/>. Implementations typically
/// issue <c>madvise(MADV_DONTNEED)</c> on the evicted page so the kernel can drop it.
/// </summary>
public interface IPageEvictionHandler
{
    void OnPageEvicted(int arenaId, int pageIdx);
}

/// <summary>
/// Direct-mapped page-tracking cache for arena-backed mmap regions. Each slot occupies a full
/// 64-byte cache line; the slot value packs <c>(arenaId &lt;&lt; 32) | pageIdx</c> with
/// <c>-1L</c> as the empty sentinel. <see cref="Touch"/> hashes the key to a slot and
/// unconditionally CAS-replaces the occupant via <see cref="Interlocked.Exchange(ref long, long)"/>;
/// the displaced key is reported to the eviction handler so the caller can
/// <c>madvise(MADV_DONTNEED)</c> the page. There is no LRU or clock arm: collision is the
/// eviction policy.
/// </summary>
/// <remarks>
/// Lock-free and false-sharing-free: slots are 64-byte aligned and stride one per cache line,
/// so two threads writing to different slots never invalidate each other's L1 lines. The
/// underlying buffer is allocated off-GC via
/// <see cref="System.Runtime.InteropServices.NativeMemory.AlignedAlloc(nuint, nuint)"/> and freed
/// in <see cref="Dispose"/> (or a finalizer fallback).
///
/// Two threads racing on the same slot may each observe a different prior occupant and so each
/// fire <see cref="IPageEvictionHandler.OnPageEvicted"/> for the page they displaced. Redundant
/// <c>madvise(DONTNEED)</c> on the same page is wasted work but harmless.
/// </remarks>
public sealed unsafe class PageSlotCache : IDisposable
{
    private const long EmptySlot = -1L;
    private const int CacheLineBytes = 64;
    private const int SlotShift = 3; // log2(CacheLineBytes / sizeof(long))

    // Naturally 64-byte aligned via NativeMemory.AlignedAlloc; one long per cache line.
    private long* _slots;
    private int _disposed;
    private readonly int _slotCount;
    private readonly int _mask;
    private readonly IPageEvictionHandler _evictionHandler;

    public int MaxCapacity => _slotCount;

    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _slotCount; i++)
                if (Volatile.Read(ref SlotRef(i)) != EmptySlot) count++;
            return count;
        }
    }

    public PageSlotCache(int maxCapacity, IPageEvictionHandler evictionHandler)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);
        ArgumentNullException.ThrowIfNull(evictionHandler);
        _evictionHandler = evictionHandler;

        if (maxCapacity == 0)
        {
            _slots = null;
            _slotCount = 0;
            _mask = 0;
            return;
        }

        _slotCount = (int)BitOperations.RoundUpToPowerOf2((uint)maxCapacity);
        _mask = _slotCount - 1;

        nuint bytes = (nuint)_slotCount * CacheLineBytes;
        _slots = (long*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(bytes, CacheLineBytes);
        for (int i = 0; i < _slotCount; i++) SlotRef(i) = EmptySlot;
    }

    public void Touch(int arenaId, int pageIdx)
    {
        if (_slotCount == 0) return;

        long packed = Pack(arenaId, pageIdx);
        int idx = (int)(Mix(packed) & (uint)_mask);
        ref long slot = ref SlotRef(idx);

        // A relaxed read first lets the common no-op-on-hit path skip the bus-locking exchange.
        if (Volatile.Read(ref slot) == packed) return;

        long prev = Interlocked.Exchange(ref slot, packed);
        if (prev == EmptySlot || prev == packed) return;
        _evictionHandler.OnPageEvicted((int)(prev >> 32), (int)prev);
    }

    internal bool ContainsPage(int arenaId, int pageIdx)
    {
        if (_slotCount == 0) return false;
        long packed = Pack(arenaId, pageIdx);
        int idx = (int)(Mix(packed) & (uint)_mask);
        return Volatile.Read(ref SlotRef(idx)) == packed;
    }

    public void Clear()
    {
        for (int i = 0; i < _slotCount; i++)
            Volatile.Write(ref SlotRef(i), EmptySlot);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_slots is not null)
        {
            System.Runtime.InteropServices.NativeMemory.AlignedFree(_slots);
            _slots = null;
        }
        GC.SuppressFinalize(this);
    }

    ~PageSlotCache() => Dispose();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref long SlotRef(int slotIdx) =>
        ref Unsafe.AsRef<long>(_slots + ((nint)slotIdx << SlotShift));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(int arenaId, int pageIdx) =>
        ((long)(uint)arenaId << 32) | (uint)pageIdx;

    // Multiplicative (Fibonacci) mix; uses the high bits, which give a better
    // slot distribution than the low bits of (arenaId, pageIdx) when arenaId is
    // in {0..few} and pageIdx is a dense counter.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mix(long packed) =>
        (uint)(((ulong)packed * 0x9E3779B97F4A7C15UL) >> 32);
}
