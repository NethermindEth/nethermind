// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace Nethermind.Core.Collections;

/// <summary>
/// A high-performance 2-way skew-associative cache using a seqlock-style header per entry.
///
/// Design goals:
/// - Lock-free reads (seqlock pattern) - readers never take locks.
/// - Best-effort writes - writers skip on contention.
/// - O(1) logical Clear() via a global epoch (no per-entry zeroing).
/// - 2-way skew-associative: each way uses independent hash bits for set indexing,
///   breaking correlation between ways ("power of two choices"). Keys that collide
///   in way 0 scatter to different sets in way 1, virtually eliminating conflict misses.
///
/// Hash bit partitioning (64-bit hash, default setsLog2=14):
///   Bits  0..setsLog2-1:          way 0 set index
///   Bits  setsLog2..setsLog2+27:  hash signature stored in header (28 bits)
///   Bits  setsLog2+28..63:        way 1 set index
///
/// Header layout (64-bit):
/// [Lock:1][Epoch:26][Hash:28][Seq:8][Occ:1]
/// - Lock (bit 63): set during writes - readers retry/miss
/// - Epoch (bits 37-62): global epoch tag - changes on Clear()
/// - Hash  (bits  9-36): per-bucket hash signature (28 bits)
/// - Seq   (bits  1- 8): per-entry sequence counter (8 bits) - increments on every successful write
/// - Occ   (bit   0): occupied flag - set when slot contains valid data (value may still be null)
///
/// Array layout: [way0_set0..way0_setN, way1_set0..way1_setN] (split, not interleaved).
/// </summary>
/// <typeparam name="TKey">The key type (struct implementing IHash64bit)</typeparam>
/// <typeparam name="TValue">The value type (reference type, nullable allowed)</typeparam>
public sealed class SeqlockCache<TKey, TValue>
    where TKey : struct, IHash64bit<TKey>
    where TValue : class?
{
    // Header bit layout (fixed, independent of set count):
    // [Lock:1][Epoch:26][Hash:28][Seq:8][Occ:1]

    private const long LockMarker = unchecked((long)0x8000_0000_0000_0000); // bit 63

    private const int EpochShift = 37;
    private const long EpochMask = 0x7FFF_FFE0_0000_0000;                  // bits 37-62 (26 bits)

    private const long HashMask = 0x0000_0001_FFFF_FE00;                   // bits 9-36 (28 bits)

    private const long SeqMask = 0x0000_0000_0000_01FE;                    // bits 1-8 (8 bits)
    private const long SeqInc = 0x0000_0000_0000_0002;                    // +1 in seq field

    private const long OccupiedBit = 1L;                                   // bit 0

    // Mask of all "identity" bits for an entry, excluding Lock and Seq.
    private const long TagMask = EpochMask | HashMask | OccupiedBit;

    // Mask for checking if an entry is live in the current epoch.
    private const long EpochOccMask = EpochMask | OccupiedBit;

    /// <summary>
    /// Number of sets (power of 2). Each set has 2 ways.
    /// </summary>
    private readonly int _sets;
    private readonly int _setMask;

    /// <summary>
    /// How many bits to right-shift the hash code to extract the signature for the header.
    /// Maps hash code bit [setsLog2] to header bit 9.
    /// </summary>
    private readonly int _hashShift;

    /// <summary>
    /// Bit position in the hash code where way 1's set index starts.
    /// Equals setsLog2 + 28 (after way 0 bits and hash signature bits).
    /// </summary>
    private readonly int _way1Shift;

    /// <summary>
    /// Array of entries: [way0_set0..way0_setN, way1_set0..way1_setN].
    /// Split layout ensures each way is a contiguous block for better prefetch behavior.
    /// </summary>
    private readonly Entry[] _entries;

    /// <summary>
    /// Current epoch counter (unshifted, informational / debugging).
    /// </summary>
    private long _epoch;

    /// <summary>
    /// Pre-shifted epoch tag: (_epoch &lt;&lt; EpochShift) &amp; EpochMask.
    /// Readers use this directly to avoid shift/mask in the hot path.
    /// </summary>
    private long _shiftedEpoch;

    /// <summary>
    /// Creates a cache with the default size (32768 entries = 16384 sets × 2 ways).
    /// </summary>
    public SeqlockCache() : this(14) { }

    /// <summary>
    /// Creates a cache with a configurable number of sets.
    /// </summary>
    /// <param name="setsLog2">Log2 of the number of sets. Must be between 8 and 18.
    /// Total entries = 2^(setsLog2+1). Default 14 = 32K entries, 16 = 128K entries.</param>
    public SeqlockCache(int setsLog2)
    {
        if (setsLog2 < 8 || setsLog2 > 18)
            throw new ArgumentOutOfRangeException(nameof(setsLog2), "Must be between 8 and 18");

        _sets = 1 << setsLog2;
        _setMask = _sets - 1;
        _hashShift = setsLog2 - 9;
        _way1Shift = setsLog2 + 28;
        _entries = new Entry[_sets << 1]; // sets * 2
        _epoch = 0;
        _shiftedEpoch = 0;
    }

    /// <summary>
    /// Tries to get a value from the cache using a seqlock pattern (lock-free reads).
    /// Checks both ways of the target set for the key.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe bool TryGetValue(in TKey key, out TValue? value)
    {
        long hashCode = key.GetHashCode64();
        int sets = _sets;
        int setMask = _setMask;
        int idx0 = (int)hashCode & setMask;
        int idx1 = sets + ((int)(hashCode >> _way1Shift) & setMask);

        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long hashPart = (hashCode >> _hashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        // Prefetch way 1 while we check way 0 — hides L2/L3 latency for skew layout.
        if (Sse.IsSupported)
        {
            Sse.PrefetchNonTemporal(Unsafe.AsPointer(ref Unsafe.Add(ref entries, idx1)));
        }

        // === Way 0 ===
        ref Entry e0 = ref Unsafe.Add(ref entries, idx0);
        long h1 = Volatile.Read(ref e0.HashEpochSeqLock);

        if ((h1 & (TagMask | LockMarker)) == expectedTag)
        {
            ref readonly TKey storedKey = ref e0.Key;
            TValue? storedValue = e0.Value;

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
            ref readonly TKey storedKey = ref e1.Key;
            TValue? storedValue = e1.Value;

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

    /// <summary>
    /// Delegate-based factory that avoids copying large keys (passes by in).
    /// Prefer this over Func&lt;TKey, TValue?&gt; when TKey is big (eg 48 bytes).
    /// </summary>
    public delegate TValue? ValueFactory(in TKey key);

    /// <summary>
    /// Gets a value from the cache, or adds it using the factory if not present.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? GetOrAdd(in TKey key, ValueFactory valueFactory)
    {
        long hashCode = key.GetHashCode64();
        int setMask = _setMask;
        int idx0 = (int)hashCode & setMask;
        int idx1 = _sets + ((int)(hashCode >> _way1Shift) & setMask);
        long hashPart = (hashCode >> _hashShift) & HashMask;

        if (TryGetValueCore(in key, idx0, idx1, hashPart, out TValue? value))
        {
            return value;
        }

        return GetOrAddMiss(in key, valueFactory, idx0, idx1, hashPart);
    }

    /// <summary>
    /// Cold path for GetOrAdd: invokes factory and stores the result.
    /// Kept out-of-line so the hot path (cache hit) compiles to a lean method body
    /// with minimal register saves and stack frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private TValue? GetOrAddMiss(in TKey key, ValueFactory valueFactory, int idx0, int idx1, long hashPart)
    {
        TValue? value = valueFactory(in key);
        SetCore(in key, value, idx0, idx1, hashPart);
        return value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe bool TryGetValueCore(in TKey key, int idx0, int idx1, long hashPart, out TValue? value)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        if (Sse.IsSupported)
        {
            Sse.PrefetchNonTemporal(Unsafe.AsPointer(ref Unsafe.Add(ref entries, idx1)));
        }

        // Way 0
        ref Entry e0 = ref Unsafe.Add(ref entries, idx0);
        long h1 = Volatile.Read(ref e0.HashEpochSeqLock);

        if ((h1 & (TagMask | LockMarker)) == expectedTag)
        {
            ref readonly TKey storedKey = ref e0.Key;
            TValue? storedValue = e0.Value;

            long h2 = Volatile.Read(ref e0.HashEpochSeqLock);
            if (h1 == h2 && storedKey.Equals(in key))
            {
                value = storedValue;
                return true;
            }
        }

        // Way 1
        ref Entry e1 = ref Unsafe.Add(ref entries, idx1);
        long w1 = Volatile.Read(ref e1.HashEpochSeqLock);

        if ((w1 & (TagMask | LockMarker)) == expectedTag)
        {
            ref readonly TKey storedKey = ref e1.Key;
            TValue? storedValue = e1.Value;

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
    private void SetCore(in TKey key, TValue? value, int idx0, int idx1, long hashPart)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long tagToStore = epochTag | hashPart | OccupiedBit;
        long epochOccTag = epochTag | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);
        ref Entry e0 = ref Unsafe.Add(ref entries, idx0);

        long h0 = Volatile.Read(ref e0.HashEpochSeqLock);

        // === Way 0: check for matching key ===
        if (h0 >= 0 && (h0 & TagMask) == tagToStore)
        {
            ref readonly TKey k0 = ref e0.Key;
            TValue? v0 = e0.Value;

            long h0_2 = Volatile.Read(ref e0.HashEpochSeqLock);
            if (h0 == h0_2 && k0.Equals(in key))
            {
                if (ReferenceEquals(v0, value)) return; // fast-path: same key+value, no-op
                WriteEntry(ref e0, h0_2, in key, value, tagToStore);
                return;
            }
            h0 = h0_2;
        }

        // === Way 1: check for matching key ===
        ref Entry e1 = ref Unsafe.Add(ref entries, idx1);
        long h1 = Volatile.Read(ref e1.HashEpochSeqLock);

        if (h1 >= 0 && (h1 & TagMask) == tagToStore)
        {
            ref readonly TKey k1 = ref e1.Key;
            TValue? v1 = e1.Value;

            long h1_2 = Volatile.Read(ref e1.HashEpochSeqLock);
            if (h1 == h1_2 && k1.Equals(in key))
            {
                if (ReferenceEquals(v1, value)) return; // fast-path: same key+value, no-op
                WriteEntry(ref e1, h1_2, in key, value, tagToStore);
                return;
            }
            h1 = h1_2;
        }

        // === Key not in either way. Evict into an available slot. ===
        // Priority: stale/empty unlocked > live (alternating by hash bit) > any unlocked > skip.
        // The decision tree selects which way to evict into, then issues a single WriteEntry call.
        bool h0Live = h0 >= 0 && (h0 & EpochOccMask) == epochOccTag;
        bool h1Live = h1 >= 0 && (h1 & EpochOccMask) == epochOccTag;

        bool pick0;
        if (!h0Live && h0 >= 0) pick0 = true;
        else if (!h1Live && h1 >= 0) pick0 = false;
        else if (h0Live && h1Live) pick0 = (hashPart & (1L << 9)) != 0;
        else if (h0 >= 0) pick0 = true;
        else if (h1 >= 0) pick0 = false;
        else return; // both locked, skip

        WriteEntry(
            ref pick0 ? ref e0 : ref e1,
            pick0 ? h0 : h1,
            in key, value, tagToStore);
    }

    /// <summary>
    /// Sets a key-value pair in the cache.
    /// Checks both ways of the target set for an existing key match before evicting.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(in TKey key, TValue? value)
    {
        long hashCode = key.GetHashCode64();
        int setMask = _setMask;
        int idx0 = (int)hashCode & setMask;
        int idx1 = _sets + ((int)(hashCode >> _way1Shift) & setMask);
        long hashPart = (hashCode >> _hashShift) & HashMask;

        SetCore(in key, value, idx0, idx1, hashPart);
    }

    /// <summary>
    /// Attempts a CAS-guarded write to a single entry.
    /// Kept out-of-line: the CAS atomic dominates latency, so call overhead is invisible,
    /// while de-duplication reclaims ~350 bytes of inlined copies across SetCore call sites.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteEntry(ref Entry entry, long existing, in TKey key, TValue? value, long tagToStore)
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
    /// Entries with stale epochs are treated as empty on subsequent lookups.
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

    /// <summary>
    /// Cache entry struct.
    /// Header is a single 64-bit field to keep the seqlock control word in one atomic unit.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public long HashEpochSeqLock; // [Lock|Epoch|Hash|Seq|Occ]
        public TKey Key;
        public TValue? Value;
    }
}
