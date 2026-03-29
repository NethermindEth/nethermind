// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Collections;

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

    // Header bit layout: [Lock:1][Epoch:26][Hash:20][Seq:16][Occ:1]
    private const long LockMarker = unchecked((long)0x8000_0000_0000_0000);

    private const int EpochShift = 37;
    private const long EpochMask = 0x7FFF_FFE0_0000_0000;

    private const long HashMask = 0x0000_001F_FFFE_0000;

    private const long SeqMask = 0x0000_0000_0001_FFFE;
    private const long SeqInc = 0x0000_0000_0000_0002;

    private const long OccupiedBit = 1L;

    private const long TagMask = EpochMask | HashMask | OccupiedBit;
    private const long EpochOccMask = EpochMask | OccupiedBit;

    private const int HashShift = 5;

    private readonly Entry[] _entries;
    private readonly int _setMask;
    private readonly int _setCount;
    private readonly int[] _setGates;
    private long _shiftedEpoch;
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public AssociativeKeyCache(int maxCapacity)
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

        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long hashPart = (hashCode >> HashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        for (int i = 0; i < Ways; i++)
        {
            ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
            long h1 = Volatile.Read(ref e.Header);

            if ((h1 & (TagMask | LockMarker)) != expectedTag) continue;

            TKey storedKey = e.Key;

            long h2 = Volatile.Read(ref e.Header);
            if (h1 == h2 && key.Equals(in storedKey))
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
        long hashPart = (hashCode >> HashShift) & HashMask;

        AcquireSetGate(setIndex);
        try
        {
            return SetCore(in key, baseIdx, hashPart);
        }
        finally
        {
            ReleaseSetGate(setIndex);
        }
    }

    private bool SetCore(in TKey key, int baseIdx, long hashPart)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long tagToStore = epochTag | hashPart | OccupiedBit;
        long epochOccTag = epochTag | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        int bestEmpty = -1;
        int bestStale = -1;
        long now = Stopwatch.GetTimestamp();

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
                    // Already present — update ticker
                    e.Ticker = now;
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
            target = Pick3RandomEvict(ref entries, baseIdx, now);
        }

        ref Entry te = ref Unsafe.Add(ref entries, baseIdx + target);
        long existing = Volatile.Read(ref te.Header);

        if ((existing & EpochOccMask) == epochOccTag)
        {
            Interlocked.Decrement(ref _count);
        }

        WriteEntry(ref te, existing, in key, tagToStore, now);
        Interlocked.Increment(ref _count);
        return true;
    }

    public bool Delete(in TKey key)
    {
        if (_setCount == 0) return false;

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;
        long hashPart = (hashCode >> HashShift) & HashMask;

        AcquireSetGate(setIndex);
        try
        {
            return DeleteCore(in key, baseIdx, hashPart);
        }
        finally
        {
            ReleaseSetGate(setIndex);
        }
    }

    private bool DeleteCore(in TKey key, int baseIdx, long hashPart)
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
                long newSeq = ((h & SeqMask) + SeqInc) & SeqMask;
                long lockedHeader = (h & EpochMask) | newSeq | LockMarker;

                Volatile.Write(ref e.Header, lockedHeader);

                e.Key = default;
                e.Ticker = 0;

                Volatile.Write(ref e.Header, (h & EpochMask) | newSeq);

                Interlocked.Decrement(ref _count);
                return true;
            }
        }

        return false;
    }

    public void Clear()
    {
        if (_setCount == 0) return;

        long oldShifted = Volatile.Read(ref _shiftedEpoch);

        while (true)
        {
            long oldEpoch = (oldShifted & EpochMask) >> EpochShift;
            long newEpoch = oldEpoch + 1;
            long newShifted = (newEpoch << EpochShift) & EpochMask;

            long prev = Interlocked.CompareExchange(ref _shiftedEpoch, newShifted, oldShifted);
            if (prev == oldShifted)
            {
                Volatile.Write(ref _count, 0);
                return;
            }

            oldShifted = prev;
        }
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
        long hashPart = (hashCode >> HashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        for (int i = 0; i < Ways; i++)
        {
            ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
            long h1 = Volatile.Read(ref e.Header);

            if ((h1 & (TagMask | LockMarker)) != expectedTag) continue;

            TKey storedKey = e.Key;

            long h2 = Volatile.Read(ref e.Header);
            if (h1 == h2 && key.Equals(in storedKey))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Pick3RandomEvict(ref Entry entries, int baseIdx, long now)
    {
        uint r = (uint)now;
        int a = (int)(r & 0x7);
        int b = (int)((r >> 3) & 0x7);
        int c = (int)((r >> 6) & 0x7);

        if (b == a) b = (a + 1) & 0x7;
        if (c == a) c = (a + 2) & 0x7;
        if (c == b) c = (b + 1) & 0x7;
        if (c == a) c = (a + 3) & 0x7;

        long ta = Unsafe.Add(ref entries, baseIdx + a).Ticker;
        long tb = Unsafe.Add(ref entries, baseIdx + b).Ticker;
        long tc = Unsafe.Add(ref entries, baseIdx + c).Ticker;

        if (ta <= tb && ta <= tc) return a;
        if (tb <= tc) return b;
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteEntry(ref Entry entry, long existing, in TKey key, long tagToStore, long ticker)
    {
        long newSeq = ((existing & SeqMask) + SeqInc) & SeqMask;
        long lockedHeader = tagToStore | newSeq | LockMarker;

        Volatile.Write(ref entry.Header, lockedHeader);

        entry.Key = key;
        entry.Ticker = ticker;

        Volatile.Write(ref entry.Header, tagToStore | newSeq);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AcquireSetGate(int setIndex)
    {
        ref int gate = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_setGates), setIndex);
        SpinWait sw = default;
        while (Interlocked.CompareExchange(ref gate, 1, 0) != 0)
        {
            sw.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReleaseSetGate(int setIndex)
    {
        ref int gate = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_setGates), setIndex);
        Volatile.Write(ref gate, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public long Header;
        public long Ticker;
        public TKey Key;
    }
}
