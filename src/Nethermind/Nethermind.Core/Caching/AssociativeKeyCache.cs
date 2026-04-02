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
    private long _ticker;

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
        _setCount = (int)BitOperations.RoundUpToPowerOf2((uint)((maxCapacity + Ways - 1) / Ways));
        _setMask = _setCount - 1;
        _hashShift = BitOperations.Log2((uint)_setCount);
        _entries = new Entry[_setCount * Ways];
        // Gate integers are packed (16 per cache line). Writers to different sets on the same
        // cache line cause false sharing, but writes are infrequent relative to lock-free reads,
        // so the memory cost of 64-byte padding per gate is not justified for current usage.
        _setGates = new int[_setCount];
    }

    /// <summary>Lookup that refreshes the eviction ticker on hit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(in TKey key) => GetCore<OnFlag>(in key);

    /// <summary>Lookup without refreshing the eviction ticker. Use for Set-then-Get patterns.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetNoRefresh(in TKey key) => GetCore<OffFlag>(in key);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GetCore<TRefreshTicker>(in TKey key)
        where TRefreshTicker : struct, IFlag
    {
        if (_setCount == 0) return false;

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

            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            TKey storedKey = e.Key;
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
        long hashPart = ExtractHashPart(hashCode, _hashShift);

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
                        // Key already present — just refresh the eviction ticker.
                        // Unlike AssociativeCache.SetCore (which calls WriteEntry to update the value),
                        // the key-only variant has nothing to write, so a bare ticker store suffices.
                        // The seqlock header is unchanged, which is correct: readers see a stable entry.
                        e.Ticker = Interlocked.Increment(ref _ticker);
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

            WriteEntry(ref te, existing, in key, tagToStore, timestamp);
            AdjustCountIfEpoch(ref _epochAndCount, epochTag, evictingLive ? 0 : 1);

            // Final check: if Clear() raced after the write, the entry has a stale epoch
            // tag and is invisible to readers. AdjustCountIfEpoch already skipped the
            // count update (epoch mismatch). Retry to write with the current epoch.
            if (ReadEpoch(ref _epochAndCount) != epochTag) continue;

            return true;
        }
    }

    public bool Delete(in TKey key)
    {
        if (_setCount == 0) return false;

        long hashCode = key.GetHashCode64();
        int setIndex = (int)hashCode & _setMask;
        int baseIdx = setIndex << WayShift;
        long hashPart = ExtractHashPart(hashCode, _hashShift);

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

                AdjustCountIfEpoch(ref _epochAndCount, epochTag, -1);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Logically invalidates all entries via O(1) epoch bump.
    /// When <paramref name="releaseReferences"/> is true (the default) and <typeparamref name="TKey"/>
    /// contains references, also walks each set under its gate to null out Key fields.
    /// Pass false on hot paths where O(1) clear is preferred and stale GC roots are acceptable.
    /// </summary>
    public void Clear(bool releaseReferences = true)
    {
        if (_setCount != 0)
        {
            long currentEpoch = ClearEpochAndCount(ref _epochAndCount);

            if (releaseReferences && RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
            {
                ClearEntries(currentEpoch);
            }
        }
    }

    /// <remarks>
    /// If a concurrent Clear() bumps the epoch again while this scan is in progress,
    /// entries from our epoch that were written between the two bumps will be skipped
    /// by both scans. Those entries are logically dead but remain GC-rooted until
    /// overwritten by new inserts. This is benign — not a safety issue.
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

                    // Seqlock write: lock → null Key → unlock (clears OccupiedBit)
                    long newSeq = ((h & SeqMask) + SeqInc) & SeqMask;
                    Volatile.Write(ref e.Header, (h & EpochMask) | newSeq | LockMarker);
                    if (!Sse.IsSupported) Interlocked.MemoryBarrier();

                    e.Key = default;
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

        // STLR on ARM64 is release-only: subsequent stores can move before it.
        // Barrier ensures Key/Ticker stores stay after the lock.
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
