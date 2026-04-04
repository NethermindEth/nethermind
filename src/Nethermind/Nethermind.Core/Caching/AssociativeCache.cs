// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
///   <item><term>Clear</term><description>O(n) zeroing / O(n) zeroing / O(1) epoch bump + optional O(n) reference release</description></item>
/// </list>
///
/// <para><b>Why <c>TValue : class?</c>:</b> The seqlock read path copies entry fields (Key, Value)
/// without holding a lock, then validates via sequence number. A reference-type Value is a single
/// pointer-width store/load — atomic on both x64 and ARM64. A multi-byte value struct (e.g. 72-byte
/// AccountStruct) is NOT atomic: a concurrent reader could observe a partially-written struct where
/// some fields belong to the old value and others to the new. The seqlock retry would usually catch
/// this, but if the writer completes the struct write and bumps the closing sequence between the
/// reader's first and last field copies, the reader sees a valid sequence with torn data.
/// Use <see cref="ClockCache{TKey,TValue}"/> for value-type TValues (it uses an McsLock that
/// makes reads and writes mutually exclusive, preventing tearing).</para>
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

    /// <summary>
    /// Combined [Epoch:26][Count:37] field. Epoch occupies bits 37-62 (same as entry headers).
    /// Count occupies bits 0-36. Atomic CAS in Clear() bumps epoch + resets count in one operation.
    /// </summary>
    private long _epochAndCount;

    /// <summary>
    /// Monotonic counter for eviction-age tracking. Interlocked.Increment is faster
    /// than Stopwatch.GetTimestamp() (RDTSC) — ~7.6ns vs ~19ns single-threaded,
    /// and scales better under contention (20% faster at 8 threads).
    /// </summary>
    private long _ticker;

    public int Count => ReadCount(ref _epochAndCount);

    public AssociativeCache(int maxCapacity)
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
        _setCount = (int)BitOperations.RoundUpToPowerOf2((uint)((maxCapacity + Ways - 1) / Ways));
        _setMask = _setCount - 1;
        _hashShift = BitOperations.Log2((uint)_setCount);
        _entries = new Entry[_setCount * Ways];
        // Gate integers are packed (16 per cache line). Writers to different sets on the same
        // cache line cause false sharing, but writes are infrequent relative to lock-free reads,
        // so the memory cost of 64-byte padding per gate is not justified for current usage.
        _setGates = new int[_setCount];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? Get(in TKey key)
    {
        TryGet(in key, out TValue? value);
        return value;
    }

    /// <summary>Lookup that refreshes the eviction ticker on hit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(in TKey key, out TValue? value)
        => TryGetCore<OnFlag>(in key, out value);

    /// <summary>Lookup without refreshing the eviction ticker. Use for Set-then-Get patterns.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetNoRefresh(in TKey key, out TValue? value)
        => TryGetCore<OffFlag>(in key, out value);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetCore<TRefreshTicker>(in TKey key, out TValue? value)
        where TRefreshTicker : struct, IFlag
    {
        if (_setCount == 0)
        {
            value = null;
            return false;
        }

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;

        long epochTag = ReadEpoch(ref _epochAndCount);
        long hashPart = ExtractHashPart(hashCode, _hashShift);
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
            if (h1 == h2 && storedKey.Equals(in key))
            {
                // JIT eliminates this branch entirely per TRefreshTicker instantiation.
                // Ticker store without the set gate is safe: 8-byte aligned long is atomic on
                // x64/ARM64 hardware. A race with a concurrent Set only affects eviction ranking,
                // not key/value correctness — the "losing" ticker value is simply slightly stale.
                if (TRefreshTicker.IsActive)
                    e.Ticker = Interlocked.Increment(ref _ticker);
                value = storedValue;
                return true;
            }
        }

        value = null;
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
        long hashPart = ExtractHashPart(hashCode, _hashShift);

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
        // Retry with fresh epoch if Clear() races at any point — never drop an insert.
        while (true)
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
                        long now = Interlocked.Increment(ref _ticker);
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

            if (ReadEpoch(ref _epochAndCount) != epochTag) continue;

            long timestamp = Interlocked.Increment(ref _ticker);
            int target = bestEmpty >= 0
                ? bestEmpty
                : bestStale >= 0
                    ? bestStale
                    : Pick3RandomEvictEntry(ref entries, baseIdx, timestamp);

            ref Entry te = ref Unsafe.Add(ref entries, baseIdx + target);
            long existing = Volatile.Read(ref te.Header);

            bool evictingLive = (existing & EpochOccMask) == epochOccTag;

            WriteEntry(ref te, existing, in key, val, tagToStore, timestamp);
            AdjustCountIfEpoch(ref _epochAndCount, epochTag, evictingLive ? 0 : 1);

            // Final check: if Clear() raced after the write, the entry has a stale epoch
            // tag and is invisible to readers. AdjustCountIfEpoch already skipped the
            // count update (epoch mismatch). Retry to write with the current epoch.
            if (ReadEpoch(ref _epochAndCount) != epochTag) continue;

            return true;
        }
    }

    public bool Delete(in TKey key) => Delete(in key, out _);

    public bool Delete(in TKey key, out TValue? value)
    {
        if (_setCount == 0)
        {
            value = null;
            return false;
        }

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;
        long hashPart = ExtractHashPart(hashCode, _hashShift);

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
                value = e.Value;

                long newSeq = ((h & SeqMask) + SeqInc) & SeqMask;
                long lockedHeader = (h & EpochMask) | newSeq | LockMarker;

                Volatile.Write(ref e.Header, lockedHeader);
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();

                e.Key = default;
                e.Value = null;
                e.Ticker = 0;

                Volatile.Write(ref e.Header, (h & EpochMask) | newSeq);

                AdjustCountIfEpoch(ref _epochAndCount, epochTag, -1);
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Logically invalidates all entries via O(1) epoch bump.
    /// When <paramref name="releaseReferences"/> is true (the default), also walks each set
    /// under its gate to null out Key/Value fields so the GC can reclaim referenced objects.
    /// Pass false on hot paths where O(1) clear is preferred and stale GC roots are acceptable.
    /// </summary>
    public void Clear(bool releaseReferences = true)
    {
        if (_setCount != 0)
        {
            long currentEpoch = ClearEpochAndCount(ref _epochAndCount);

            if (releaseReferences)
            {
                ClearEntries(currentEpoch);
            }
        }
    }

    /// <remarks>
    /// If a concurrent Clear() bumps the epoch again while this scan is in progress,
    /// entries from our epoch (N+1) that were written between the two bumps will be
    /// skipped by both scans (neither N+1 nor N+2 matches the other's snapshot).
    /// Those entries are logically dead (invisible to readers) but remain GC-rooted
    /// until overwritten by new inserts. This is benign — not a safety issue.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ClearEntries(long currentEpoch)
    {
        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);
        ref int gates = ref MemoryMarshal.GetArrayDataReference(_setGates);

        for (int s = 0; s < _setCount; s++)
        {
            ref int gate = ref Unsafe.Add(ref gates, s);
            AcquireGate(ref gate);
            try
            {
                int baseIdx = s << WayShift;
                for (int i = 0; i < Ways; i++)
                {
                    ref Entry e = ref Unsafe.Add(ref entries, baseIdx + i);
                    long h = Volatile.Read(ref e.Header);

                    // Skip empty entries and entries written after the epoch bump
                    if ((h & OccupiedBit) == 0) continue;
                    if ((h & EpochMask) == currentEpoch) continue;

                    // Seqlock write: lock → null fields → unlock (clears OccupiedBit)
                    long newSeq = ((h & SeqMask) + SeqInc) & SeqMask;
                    Volatile.Write(ref e.Header, (h & EpochMask) | newSeq | LockMarker);
                    if (!Sse.IsSupported) Interlocked.MemoryBarrier();

                    if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                        e.Key = default;
                    e.Value = null;
                    e.Ticker = 0;

                    Volatile.Write(ref e.Header, (h & EpochMask) | newSeq);
                }
            }
            finally
            {
                ReleaseGate(ref gate);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(in TKey key) => TryGetNoRefresh(in key, out _);

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
