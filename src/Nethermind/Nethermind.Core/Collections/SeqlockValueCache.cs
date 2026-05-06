// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace Nethermind.Core.Collections;

/// <summary>
/// Struct-value variant of <see cref="SeqlockCache{TKey, TValue}"/>: 2-way skew-associative
/// cache with seqlock-style headers, for value-type values.
///
/// Differs from <see cref="SeqlockCache{TKey, TValue}"/> in two ways:
/// - <typeparamref name="TValue"/> is a struct (no boxing on Set).
/// - Set count is configurable via the constructor (must be a positive power of two).
///   Use this when 32k×2 entries is too large; pick the smallest power of two that
///   fits the working set.
///
/// Header bit layout, epoch-based <see cref="Clear"/>, and seqlock retry semantics are
/// identical to <see cref="SeqlockCache{TKey, TValue}"/>. The seqlock retry on torn-read
/// of multi-word struct values is provided by the post-read header check.
/// </summary>
/// <typeparam name="TKey">The key type (struct implementing IHash64bit)</typeparam>
/// <typeparam name="TValue">The value type (struct)</typeparam>
public sealed class SeqlockValueCache<TKey, TValue>
    where TKey : struct, IHash64bit<TKey>
    where TValue : struct
{
    // Header bit layout (same as SeqlockCache):
    // [Lock:1][Epoch:26][Hash:20][Seq:16][Occ:1]

    private const long LockMarker = unchecked((long)0x8000_0000_0000_0000); // bit 63

    private const int EpochShift = 37;
    private const long EpochMask = 0x7FFF_FFE0_0000_0000;                  // bits 37-62 (26 bits)

    private const long HashMask = 0x0000_001F_FFFE_0000;                   // bits 17-36 (20 bits)

    private const long SeqMask = 0x0000_0000_0001_FFFE;                    // bits 1-16 (16 bits)
    private const long SeqInc = 0x0000_0000_0000_0002;                    // +1 in seq field

    private const long OccupiedBit = 1L;                                   // bit 0

    private const long TagMask = EpochMask | HashMask | OccupiedBit;
    private const long EpochOccMask = EpochMask | OccupiedBit;

    private const int HashShift = 5;
    private const int Way1Shift = 42;

    private readonly int _sets;
    private readonly int _setMask;

    private readonly Entry[] _entries;

    private long _epoch;
    private long _shiftedEpoch;

    /// <summary>
    /// Construct a cache with <paramref name="sets"/> sets per way (2 ways total).
    /// </summary>
    /// <param name="sets">Number of sets. Must be a positive power of two.</param>
    public SeqlockValueCache(int sets)
    {
        if (sets <= 0 || (sets & (sets - 1)) != 0)
            throw new ArgumentException("sets must be a positive power of two", nameof(sets));

        _sets = sets;
        _setMask = sets - 1;
        _entries = new Entry[sets << 1]; // sets * 2
        _epoch = 0;
        _shiftedEpoch = 0;
    }

    /// <summary>
    /// Tries to get a value from the cache using a seqlock pattern (lock-free reads).
    /// Checks both ways of the target set for the key.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe bool TryGetValue(in TKey key, out TValue value)
    {
        long hashCode = key.GetHashCode64();
        int idx0 = (int)hashCode & _setMask;
        int idx1 = _sets + ((int)(hashCode >> Way1Shift) & _setMask);

        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long hashPart = (hashCode >> HashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        if (Sse.IsSupported)
        {
            Sse.PrefetchNonTemporal(Unsafe.AsPointer(ref Unsafe.Add(ref entries, idx1)));
        }

        // === Way 0 ===
        ref Entry e0 = ref Unsafe.Add(ref entries, idx0);
        long h1 = Volatile.Read(ref e0.HashEpochSeqLock);

        if ((h1 & (TagMask | LockMarker)) == expectedTag)
        {
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            TKey storedKey = e0.Key;
            TValue storedValue = e0.Value;
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            long h2 = Volatile.Read(ref e0.HashEpochSeqLock);
            if (h1 == h2 && storedKey.Equals(in key))
            {
                value = storedValue;
                return true;
            }
        }

        // === Way 1 ===
        ref Entry e1 = ref Unsafe.Add(ref entries, idx1);
        long w1 = Volatile.Read(ref e1.HashEpochSeqLock);

        if ((w1 & (TagMask | LockMarker)) == expectedTag)
        {
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            TKey storedKey = e1.Key;
            TValue storedValue = e1.Value;
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            long w2 = Volatile.Read(ref e1.HashEpochSeqLock);
            if (w1 == w2 && storedKey.Equals(in key))
            {
                value = storedValue;
                return true;
            }
        }

        value = default;
        return false;
    }

    public delegate TValue ValueFactory(in TKey key);
    public delegate TValue ValueFactory<TState>(in TKey key, TState state);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(in TKey key, ValueFactory valueFactory)
        => GetOrAdd(in key, valueFactory, static (in TKey k, ValueFactory f) => f(in k));

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd<TState>(in TKey key, TState state, ValueFactory<TState> valueFactory)
    {
        long hashCode = key.GetHashCode64();
        int idx0 = (int)hashCode & _setMask;
        int idx1 = _sets + ((int)(hashCode >> Way1Shift) & _setMask);
        long hashPart = (hashCode >> HashShift) & HashMask;

        if (TryGetValueCore(in key, idx0, idx1, hashPart, out TValue value))
        {
            return value;
        }

        return GetOrAddMiss(in key, state, valueFactory, idx0, idx1, hashPart);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private TValue GetOrAddMiss<TState>(in TKey key, TState state, ValueFactory<TState> valueFactory, int idx0, int idx1, long hashPart)
    {
        TValue value = valueFactory(in key, state);
        SetCore(in key, value, idx0, idx1, hashPart);
        return value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe bool TryGetValueCore(in TKey key, int idx0, int idx1, long hashPart, out TValue value)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        if (Sse.IsSupported)
        {
            Sse.PrefetchNonTemporal(Unsafe.AsPointer(ref Unsafe.Add(ref entries, idx1)));
        }

        ref Entry e0 = ref Unsafe.Add(ref entries, idx0);
        long h1 = Volatile.Read(ref e0.HashEpochSeqLock);

        if ((h1 & (TagMask | LockMarker)) == expectedTag)
        {
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            TKey storedKey = e0.Key;
            TValue storedValue = e0.Value;
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            long h2 = Volatile.Read(ref e0.HashEpochSeqLock);
            if (h1 == h2 && storedKey.Equals(in key))
            {
                value = storedValue;
                return true;
            }
        }

        ref Entry e1 = ref Unsafe.Add(ref entries, idx1);
        long w1 = Volatile.Read(ref e1.HashEpochSeqLock);

        if ((w1 & (TagMask | LockMarker)) == expectedTag)
        {
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();
            TKey storedKey = e1.Key;
            TValue storedValue = e1.Value;
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            long w2 = Volatile.Read(ref e1.HashEpochSeqLock);
            if (w1 == w2 && storedKey.Equals(in key))
            {
                value = storedValue;
                return true;
            }
        }

        value = default;
        return false;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetCore(in TKey key, TValue value, int idx0, int idx1, long hashPart)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long tagToStore = epochTag | hashPart | OccupiedBit;
        long epochOccTag = epochTag | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);
        ref Entry e0 = ref Unsafe.Add(ref entries, idx0);

        long h0 = Volatile.Read(ref e0.HashEpochSeqLock);

        if (h0 >= 0 && (h0 & TagMask) == tagToStore)
        {
            TKey k0 = e0.Key;
            TValue v0 = e0.Value;
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            long h0_2 = Volatile.Read(ref e0.HashEpochSeqLock);
            if (h0 == h0_2 && k0.Equals(in key))
            {
                if (EqualityComparer<TValue>.Default.Equals(v0, value)) return; // fast-path: same key+value, no-op
                WriteEntry(ref e0, h0_2, in key, value, tagToStore);
                return;
            }
            h0 = h0_2;
        }

        ref Entry e1 = ref Unsafe.Add(ref entries, idx1);
        long h1 = Volatile.Read(ref e1.HashEpochSeqLock);

        if (h1 >= 0 && (h1 & TagMask) == tagToStore)
        {
            TKey k1 = e1.Key;
            TValue v1 = e1.Value;
            if (!Sse.IsSupported) Interlocked.MemoryBarrier();

            long h1_2 = Volatile.Read(ref e1.HashEpochSeqLock);
            if (h1 == h1_2 && k1.Equals(in key))
            {
                if (EqualityComparer<TValue>.Default.Equals(v1, value)) return; // fast-path: same key+value, no-op
                WriteEntry(ref e1, h1_2, in key, value, tagToStore);
                return;
            }
            h1 = h1_2;
        }

        bool h0Live = h0 >= 0 && (h0 & EpochOccMask) == epochOccTag;
        bool h1Live = h1 >= 0 && (h1 & EpochOccMask) == epochOccTag;

        bool pick0;
        if (!h0Live && h0 >= 0) pick0 = true;
        else if (!h1Live && h1 >= 0) pick0 = false;
        else if (h0Live && h1Live) pick0 = (hashPart & (1L << 17)) != 0;
        else if (h0 >= 0) pick0 = true;
        else if (h1 >= 0) pick0 = false;
        else return; // both locked, skip

        WriteEntry(
            ref pick0 ? ref e0 : ref e1,
            pick0 ? h0 : h1,
            in key, value, tagToStore);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(in TKey key, TValue value)
    {
        long hashCode = key.GetHashCode64();
        int idx0 = (int)hashCode & _setMask;
        int idx1 = _sets + ((int)(hashCode >> Way1Shift) & _setMask);
        long hashPart = (hashCode >> HashShift) & HashMask;

        SetCore(in key, value, idx0, idx1, hashPart);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteEntry(ref Entry entry, long existing, in TKey key, TValue value, long tagToStore)
    {
        if (existing < 0) return; // locked

        long newSeq = ((existing & SeqMask) + SeqInc) & SeqMask;
        long lockedHeader = tagToStore | newSeq | LockMarker;

        if (Interlocked.CompareExchange(ref entry.HashEpochSeqLock, lockedHeader, existing) != existing)
        {
            return;
        }

        entry.Key = key;
        entry.Value = value;

        Volatile.Write(ref entry.HashEpochSeqLock, tagToStore | newSeq);
    }

    /// <summary>
    /// Clears all cached entries by incrementing the global epoch tag (O(1)).
    /// </summary>
    public void Clear()
    {
        long oldShifted = Volatile.Read(ref _shiftedEpoch);

        while (true)
        {
            long oldEpoch = (oldShifted & EpochMask) >> EpochShift;
            long newEpoch = oldEpoch + 1;
            long newShifted = (newEpoch << EpochShift) & EpochMask;

            long prev = Interlocked.CompareExchange(ref _shiftedEpoch, newShifted, oldShifted);
            if (prev == oldShifted)
            {
                Volatile.Write(ref _epoch, newEpoch);
                return;
            }

            oldShifted = prev;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public long HashEpochSeqLock; // [Lock|Epoch|Hash|Seq|Occ]
        public TKey Key;
        public TValue Value;
    }
}
