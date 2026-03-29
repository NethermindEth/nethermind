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
/// Key-only variant of <see cref="AssociativeCache{TKey,TValue}"/>.
/// 8-way set-associative cache with lock-free reads and 3-random eviction.
/// See <see cref="AssociativeCache{TKey,TValue}"/> for full design notes and tradeoff comparison.
/// </summary>
public sealed class AssociativeKeyCache<TKey>
    where TKey : struct, IHash64bit<TKey>
{
    private const int Ways = 8;
    private const int WayShift = 3;

    private readonly Entry[] _entries;
    private readonly int _setMask;
    private readonly int _setCount;
    private readonly int _hashShift;
    private readonly int[] _setGates;
    private long _epochAndCount;

    public int Count => ReadCount(ref _epochAndCount);

    public AssociativeKeyCache(int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)maxCapacity, MaxCapacity);

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

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(in TKey key)
    {
        if (_setCount == 0) return false;

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;

        long epochTag = ReadEpoch(ref _epochAndCount);
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
            if (h1 == h2 && storedKey.Equals(in key))
            {
                e.Ticker = Stopwatch.GetTimestamp();
                return true;
            }
        }

        return false;
    }

    public bool Set(in TKey key)
    {
        if (_setCount == 0) return true;

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;
        long hashPart = (hashCode >> _hashShift) & HashMask;

        ref int gate = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_setGates), setIndex);
        AcquireGate(ref gate);
        try
        {
            return SetCore(in key, baseIdx, hashPart);
        }
        finally
        {
            ReleaseGate(ref gate);
        }
    }

    private bool SetCore(in TKey key, int baseIdx, long hashPart)
    {
        long epochTag = ReadEpoch(ref _epochAndCount);
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
                    e.Ticker = Stopwatch.GetTimestamp();
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
        if (ReadEpoch(ref _epochAndCount) != epochTag) return true;

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
            Interlocked.Add(ref _epochAndCount, -1);
        }

        WriteEntry(ref te, existing, in key, tagToStore, timestamp);
        Interlocked.Add(ref _epochAndCount, 1);
        return true;
    }

    public bool Delete(in TKey key)
    {
        if (_setCount == 0) return false;

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;
        long hashPart = (hashCode >> _hashShift) & HashMask;

        ref int gate = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_setGates), setIndex);
        AcquireGate(ref gate);
        try
        {
            return DeleteCore(in key, baseIdx, hashPart);
        }
        finally
        {
            ReleaseGate(ref gate);
        }
    }

    private bool DeleteCore(in TKey key, int baseIdx, long hashPart)
    {
        long epochTag = ReadEpoch(ref _epochAndCount);
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
                long newSeq = ((h & SeqMask) + SeqInc) & SeqMask;
                long lockedHeader = (h & EpochMask) | newSeq | LockMarker;

                Volatile.Write(ref e.Header, lockedHeader);
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();

                e.Key = default;
                e.Ticker = 0;

                Volatile.Write(ref e.Header, (h & EpochMask) | newSeq);

                Interlocked.Add(ref _epochAndCount, -1);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Logically invalidates all entries via epoch bump + count reset in a single atomic CAS.
    /// No race window between epoch change and count reset.
    /// </summary>
    public void Clear()
    {
        if (_setCount == 0) return;

        ClearEpochAndCount(ref _epochAndCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(in TKey key) => Get(in key);

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
    private static void WriteEntry(ref Entry entry, long existing, in TKey key, long tagToStore, long ticker)
    {
        long newSeq = ((existing & SeqMask) + SeqInc) & SeqMask;
        long lockedHeader = tagToStore | newSeq | LockMarker;

        Volatile.Write(ref entry.Header, lockedHeader);
        if (!Sse.IsSupported) Interlocked.MemoryBarrier();

        entry.Key = key;
        entry.Ticker = ticker;

        Volatile.Write(ref entry.Header, tagToStore | newSeq);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public long Header;
        public long Ticker;
        public TKey Key;
    }
}
