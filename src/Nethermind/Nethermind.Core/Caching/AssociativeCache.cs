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
    private const int WayShift = 3; // log2(Ways)

    // Header bit layout: [Lock:1][Epoch:26][Hash:20][Seq:16][Occ:1]
    private const long LockMarker = unchecked((long)0x8000_0000_0000_0000); // bit 63

    private const int EpochShift = 37;
    private const long EpochMask = 0x7FFF_FFE0_0000_0000;  // bits 37-62 (26 bits)

    private const long HashMask = 0x0000_001F_FFFE_0000;   // bits 17-36 (20 bits)

    private const long SeqMask = 0x0000_0000_0001_FFFE;    // bits 1-16 (16 bits)
    private const long SeqInc = 0x0000_0000_0000_0002;     // +1 in seq field

    private const long OccupiedBit = 1L;                    // bit 0

    // Mask of all "identity" bits for an entry, excluding Lock and Seq.
    private const long TagMask = EpochMask | HashMask | OccupiedBit;

    // Mask for checking if an entry is live in the current epoch.
    private const long EpochOccMask = EpochMask | OccupiedBit;

    // Hash bits for set index use the low bits; hash signature uses shifted bits to avoid correlation.
    private const int HashShift = 5;

    private readonly Entry[] _entries;
    private readonly int _setMask;
    private readonly int _setCount;
    private readonly int[] _setGates; // per-set CAS spinlock: 0 = free, 1 = held
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
        _entries = new Entry[_setCount * Ways];
        _setGates = new int[_setCount];
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? Get(in TKey key)
    {
        if (_setCount == 0) return default;

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
            TValue? storedValue = e.Value;

            long h2 = Volatile.Read(ref e.Header);
            if (h1 == h2 && key.Equals(in storedKey))
            {
                e.Ticker = Stopwatch.GetTimestamp();
                return storedValue;
            }
        }

        return default;
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
        long hashPart = (hashCode >> HashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        for (int i = 0; i < Ways; i++)
        {
            ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
            long h1 = Volatile.Read(ref e.Header);

            if ((h1 & (TagMask | LockMarker)) != expectedTag) continue;

            TKey storedKey = e.Key;
            TValue? storedValue = e.Value;

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
        long hashPart = (hashCode >> HashShift) & HashMask;

        AcquireSetGate(setIndex);
        try
        {
            return SetCore(in key, val, baseIdx, hashPart);
        }
        finally
        {
            ReleaseSetGate(setIndex);
        }
    }

    private bool SetCore(in TKey key, TValue val, int baseIdx, long hashPart)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long tagToStore = epochTag | hashPart | OccupiedBit;
        long epochOccTag = epochTag | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        int bestEmpty = -1;
        int bestStale = -1;
        long now = Stopwatch.GetTimestamp();

        // Scan for existing key, empty, or stale slots
        for (int i = 0; i < Ways; i++)
        {
            ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
            long h = Volatile.Read(ref e.Header);

            // Locked by a concurrent reader-ticker-update is not possible (ticker is plain store),
            // but another writer could race. Under the gate, we are the only writer for this set.
            bool occupied = (h & OccupiedBit) != 0;
            bool currentEpoch = (h & EpochMask) == epochTag;

            if (occupied && currentEpoch)
            {
                // Check if this entry matches our key
                if ((h & HashMask) == hashPart && e.Key.Equals(in key))
                {
                    // Update existing entry
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

        // Key not found — insert into empty, stale, or evict
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
            // 3-random eviction: pick 3 distinct ways, evict the one with oldest ticker
            target = Pick3RandomEvict(ref entries, baseIdx, now);
        }

        ref Entry te = ref Unsafe.Add(ref entries, baseIdx + target);
        long existing = Volatile.Read(ref te.Header);

        // If evicting a live entry, decrement count
        if ((existing & EpochOccMask) == epochOccTag)
        {
            Interlocked.Decrement(ref _count);
        }

        WriteEntry(ref te, existing, in key, val, tagToStore, now);
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
        long hashPart = (hashCode >> HashShift) & HashMask;

        AcquireSetGate(setIndex);
        try
        {
            return DeleteCore(in key, baseIdx, hashPart, out value);
        }
        finally
        {
            ReleaseSetGate(setIndex);
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

                // Clear the entry: write header with Occ=0, keep epoch+seq incremented
                long newSeq = ((h & SeqMask) + SeqInc) & SeqMask;
                long lockedHeader = (h & EpochMask) | newSeq | LockMarker;

                // Under the gate we are the only writer, but set lock bit for readers
                Volatile.Write(ref e.Header, lockedHeader);

                e.Key = default;
                e.Value = default;
                e.Ticker = 0;

                // Unlock with Occ=0
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

        // O(1) epoch bump — stale entries treated as empty
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

    /// <summary>
    /// 3-random eviction: pick 3 distinct indices from [0, Ways), return the one with the oldest ticker.
    /// Uses mixed hash/timestamp bits to avoid Random.Shared overhead.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Pick3RandomEvict(ref Entry entries, int baseIdx, long now)
    {
        // Use low bits of timestamp as cheap entropy source
        uint r = (uint)now;
        int a = (int)(r & 0x7); // 0..7
        int b = (int)((r >> 3) & 0x7);
        int c = (int)((r >> 6) & 0x7);

        // Ensure distinct: simple fixup
        if (b == a) b = (a + 1) & 0x7;
        if (c == a) c = (a + 2) & 0x7;
        if (c == b) c = (b + 1) & 0x7;
        if (c == a) c = (a + 3) & 0x7; // final fallback

        long ta = Unsafe.Add(ref entries, baseIdx + a).Ticker;
        long tb = Unsafe.Add(ref entries, baseIdx + b).Ticker;
        long tc = Unsafe.Add(ref entries, baseIdx + c).Ticker;

        if (ta <= tb && ta <= tc) return a;
        if (tb <= tc) return b;
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteEntry(ref Entry entry, long existing, in TKey key, TValue? value, long tagToStore, long ticker)
    {
        long newSeq = ((existing & SeqMask) + SeqInc) & SeqMask;
        long lockedHeader = tagToStore | newSeq | LockMarker;

        // Set lock bit so readers skip this entry during write
        Volatile.Write(ref entry.Header, lockedHeader);

        entry.Key = key;
        entry.Value = value;
        entry.Ticker = ticker;

        // Unlock: write final header without lock bit
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
        public long Header;   // [Lock:1][Epoch:26][Hash:20][Seq:16][Occ:1]
        public long Ticker;   // Stopwatch.GetTimestamp(), separate from seqlock
        public TKey Key;
        public TValue? Value;
    }
}
