// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core.Collections;
using static Nethermind.Core.Caching.SeqlockHeader;

namespace Nethermind.Core.Caching;

/// <summary>
/// High-throughput 8-way set-associative cache with lock-free reads and
/// 3-random eviction within a set.
///
/// <para>Choose a cache based on tradeoffs:</para>
/// <list type="table">
///   <listheader>
///     <term>Cache</term>
///     <description>When to use</description>
///   </listheader>
///   <item>
///     <term>LruCache</term>
///     <description>Prefer when global LRU behavior matters more than contention.</description>
///   </item>
///   <item>
///     <term>ClockCache</term>
///     <description>Prefer when exact capacity and existing CLOCK semantics are sufficient.</description>
///   </item>
///   <item>
///     <term>AssociativeCache</term>
///     <description>Prefer on hot read-heavy paths where avoiding global coordination matters
///     more than exact global eviction order. Uses 3-random eviction within 8-way sets.</description>
///   </item>
/// </list>
///
/// <para>Key tradeoffs:</para>
/// <list type="table">
///   <listheader>
///     <term>Aspect</term>
///     <description>LruCache / ClockCache / AssociativeCache</description>
///   </listheader>
///   <item><term>Eviction scope</term><description>Global / Global / Within one 8-way set</description></item>
///   <item><term>Read path</term><description>McsLock / bitmap update / lock-free seqlock read</description></item>
///   <item><term>Write path</term><description>McsLock / global lock / set-local gate</description></item>
///   <item><term>Capacity</term><description>Exact / Exact / Rounded to setCount × 8</description></item>
/// </list>
/// </summary>
public sealed class AssociativeCache<TKey, TValue>
    where TKey : struct, IHash64bit<TKey>
    where TValue : class?
{
    private const int Ways = 8;
    private const int WayShift = 3;

    private readonly Entry[] _entries;
    private readonly int _setMask;
    private readonly int _setCount;
    private readonly int _hashShift;
    private readonly int[] _setGates;
    private long _shiftedEpoch;
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public AssociativeCache(int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);

        if (maxCapacity == 0)
        {
            _entries = [];
            _setGates = [];
            _setMask = 0;
            _setCount = 0;
            return;
        }

        _setCount = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, maxCapacity / Ways));
        _setMask = _setCount - 1;
        _hashShift = BitOperations.Log2((uint)_setCount);
        _entries = new Entry[_setCount * Ways];
        _setGates = new int[_setCount];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? Get(in TKey key)
    {
        TryGet(in key, out TValue? value);
        return value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(in TKey key, out TValue? value)
    {
        if (_setCount == 0)
        {
            value = default;
            return false;
        }

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;

        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long hashPart = (hashCode >> _hashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        for (int i = 0; i < Ways; i++)
        {
            ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
            long h1 = Volatile.Read(ref e.Header);

            if ((h1 & (TagMask | LockMarker)) != expectedTag) continue;

            // Prevent ARM64 from reordering Key/Value loads before the seqlock header read.
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            TKey storedKey = e.Key;
            TValue? storedValue = e.Value;
            // Prevent ARM64 from reordering the trailing seq re-read before Key/Value loads.
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            long h2 = Volatile.Read(ref e.Header);
            if (h1 == h2 && key.Equals(in storedKey))
            {
                e.Ticker = Stopwatch.GetTimestamp();
                value = storedValue;
                return true;
            }
        }

        value = default;
        return false;
    }

    public bool Set(in TKey key, TValue? val)
    {
        if (_setCount == 0) return true;

        if (val is null)
        {
            return Delete(in key);
        }

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;
        long hashPart = (hashCode >> _hashShift) & HashMask;

        ref int gate = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_setGates), setIndex);
        AcquireGate(ref gate);
        try
        {
            return SetCore(in key, val, baseIdx, hashPart);
        }
        finally
        {
            ReleaseGate(ref gate);
        }
    }

    private bool SetCore(in TKey key, TValue val, int baseIdx, long hashPart)
    {
        // Read epoch under the set gate; if Clear() races, we detect it below.
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long tagToStore = epochTag | hashPart | OccupiedBit;
        long epochOccTag = epochTag | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        int bestEmpty = -1;
        int bestStale = -1;

        for (int i = 0; i < Ways; i++)
        {
            ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
            long h = Volatile.Read(ref e.Header);

            bool occupied = (h & OccupiedBit) != 0;
            bool currentEpoch = (h & EpochMask) == epochTag;

            if (occupied && currentEpoch)
            {
                if ((h & HashMask) == hashPart && e.Key.Equals(in key))
                {
                    long now = Stopwatch.GetTimestamp();
                    WriteEntry(ref e, h, in key, val, tagToStore, now);
                    return false;
                }
            }
            else if (!occupied && bestEmpty < 0)
            {
                bestEmpty = i;
            }
            else if (occupied && !currentEpoch && bestStale < 0)
            {
                bestStale = i;
            }
        }

        // If epoch changed (concurrent Clear), our entry will be immediately stale — skip.
        if (Volatile.Read(ref _shiftedEpoch) != epochTag) return true;

        long timestamp = Stopwatch.GetTimestamp();
        int target;
        if (bestEmpty >= 0)
        {
            target = bestEmpty;
        }
        else if (bestStale >= 0)
        {
            target = bestStale;
        }
        else
        {
            target = Pick3RandomEvictEntry(ref entries, baseIdx, timestamp);
        }

        ref Entry te = ref Unsafe.Add(ref entries, baseIdx + target);
        long existing = Volatile.Read(ref te.Header);

        if ((existing & EpochOccMask) == epochOccTag)
        {
            Interlocked.Decrement(ref _count);
        }

        WriteEntry(ref te, existing, in key, val, tagToStore, timestamp);
        Interlocked.Increment(ref _count);
        return true;
    }

    public bool Delete(in TKey key) => Delete(in key, out _);

    public bool Delete(in TKey key, out TValue? value)
    {
        if (_setCount == 0)
        {
            value = default;
            return false;
        }

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;
        long hashPart = (hashCode >> _hashShift) & HashMask;

        ref int gate = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_setGates), setIndex);
        AcquireGate(ref gate);
        try
        {
            return DeleteCore(in key, baseIdx, hashPart, out value);
        }
        finally
        {
            ReleaseGate(ref gate);
        }
    }

    private bool DeleteCore(in TKey key, int baseIdx, long hashPart, out TValue? value)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        for (int i = 0; i < Ways; i++)
        {
            ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
            long h = Volatile.Read(ref e.Header);

            if ((h & OccupiedBit) == 0) continue;
            if ((h & EpochMask) != epochTag) continue;
            if ((h & HashMask) != hashPart) continue;

            if (e.Key.Equals(in key))
            {
                value = e.Value;

                long newSeq = ((h & SeqMask) + SeqInc) & SeqMask;
                long lockedHeader = (h & EpochMask) | newSeq | LockMarker;

                Volatile.Write(ref e.Header, lockedHeader);
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();

                e.Key = default;
                e.Value = default;
                e.Ticker = 0;

                Volatile.Write(ref e.Header, (h & EpochMask) | newSeq);

                Interlocked.Decrement(ref _count);
                return true;
            }
        }

        value = default;
        return false;
    }

    public void Clear()
    {
        if (_setCount == 0) return;

        BumpEpoch(ref _shiftedEpoch);
        Volatile.Write(ref _count, 0);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(in TKey key)
    {
        if (_setCount == 0) return false;

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;

        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long hashPart = (hashCode >> _hashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        for (int i = 0; i < Ways; i++)
        {
            ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
            long h1 = Volatile.Read(ref e.Header);

            if ((h1 & (TagMask | LockMarker)) != expectedTag) continue;

            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            TKey storedKey = e.Key;
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            long h2 = Volatile.Read(ref e.Header);
            if (h1 == h2 && key.Equals(in storedKey))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Pick3RandomEvictEntry(ref Entry entries, int baseIdx, long now)
    {
        (int a, int b, int c) = Pick3Indices(now);

        long ta = Unsafe.Add(ref entries, baseIdx + a).Ticker;
        long tb = Unsafe.Add(ref entries, baseIdx + b).Ticker;
        long tc = Unsafe.Add(ref entries, baseIdx + c).Ticker;

        return Pick3RandomEvict(ta, tb, tc, a, b, c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteEntry(ref Entry entry, long existing, in TKey key, TValue? value, long tagToStore, long ticker)
    {
        long newSeq = ((existing & SeqMask) + SeqInc) & SeqMask;
        long lockedHeader = tagToStore | newSeq | LockMarker;

        // STLR on ARM64 is release-only: subsequent stores can move before it.
        // Barrier ensures Key/Value/Ticker stores stay after the lock.
        Volatile.Write(ref entry.Header, lockedHeader);
        if (!Sse.IsSupported) Interlocked.MemoryBarrier();

        entry.Key = key;
        entry.Value = value;
        entry.Ticker = ticker;

        Volatile.Write(ref entry.Header, tagToStore | newSeq);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public long Header;
        public long Ticker;
        public TKey Key;
        public TValue? Value;
    }
}
