// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// Hash bit partitioning (64-bit hash):
///   Bits  0-13: way 0 set index (14 bits)
///   Bits 14-41: hash signature stored in header (28 bits)
///   Bits 42-55: way 1 set index (14 bits, independent from way 0)
///
/// Header layout (64-bit):
/// [Lock:1][Epoch:26][Hash:28][Seq:8][Occ:1]
/// - Lock (bit 63): set during writes - readers retry/miss
/// - Epoch (bits 37-62): global epoch tag - changes on Clear()
/// - Hash  (bits  9-36): per-bucket hash signature (28 bits)
/// - Seq   (bits  1- 8): per-entry sequence counter (8 bits) - increments on every successful write
/// - Occ   (bit   0): occupied flag - set when slot contains valid data (value may still be null)
///
/// Array layout: [way0_set0..way0_set16383, way1_set0..way1_set16383] (split, not interleaved).
/// </summary>
/// <typeparam name="TKey">The key type (struct implementing IHash64bit)</typeparam>
/// <typeparam name="TValue">The value type (reference type, nullable allowed)</typeparam>
public sealed class SeqlockCache<TKey, TValue>
    where TKey : struct, IHash64bit<TKey>
    where TValue : class?
{
    /// <summary>
    /// Number of sets. Must be a power of 2 for mask operations.
    /// 16384 sets × 2 ways = 32768 total entries.
    /// </summary>
    private const int Sets = 1 << 14; // 16384
    private const int SetMask = Sets - 1;

    // Header bit layout:
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

    // With 14-bit set index (bits 0-13) for way 0, hash signature needs bits 14+.
    // HashShift=5 maps header bits 9-36 to original bits 14-41, avoiding overlap with both ways.
    private const int HashShift = 5;

    // Way 1 uses bits 42-55 of the original hash (completely independent from way 0's bits 0-13).
    private const int Way1Shift = 42;

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

    public SeqlockCache()
    {
        _entries = new Entry[Sets << 1]; // Sets * 2
        _epoch = 0;
        _shiftedEpoch = 0;
    }

    /// <summary>
    /// Tries to get a value from the cache using a seqlock pattern (lock-free reads).
    /// Checks both ways of the target set for the key.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(in TKey key, out TValue? value)
    {
        long hashCode = key.GetHashCode64();
        int idx0 = (int)hashCode & SetMask;
        int idx1 = Sets + ((int)(hashCode >> Way1Shift) & SetMask);

        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long hashPart = (hashCode >> HashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

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
        int idx0 = (int)hashCode & SetMask;
        int idx1 = Sets + ((int)(hashCode >> Way1Shift) & SetMask);
        long hashPart = (hashCode >> HashShift) & HashMask;

        if (TryGetValueCore(in key, idx0, idx1, hashPart, out TValue? value))
        {
            return value;
        }

        value = valueFactory(in key);
        SetCore(in key, value, idx0, idx1, hashPart);
        return value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetValueCore(in TKey key, int idx0, int idx1, long hashPart, out TValue? value)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);

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
        // Prefer stale/empty entries to preserve live data.
        bool h0Live = h0 >= 0 && (h0 & EpochOccMask) == epochOccTag;
        bool h1Live = h1 >= 0 && (h1 & EpochOccMask) == epochOccTag;

        if (!h0Live && h0 >= 0)
        {
            WriteEntry(ref e0, h0, in key, value, tagToStore);
        }
        else if (!h1Live && h1 >= 0)
        {
            WriteEntry(ref e1, h1, in key, value, tagToStore);
        }
        else if (h0 >= 0)
        {
            // Both ways live, evict way 0 (newest gets the fast-read slot)
            WriteEntry(ref e0, h0, in key, value, tagToStore);
        }
        else if (h1 >= 0)
        {
            WriteEntry(ref e1, h1, in key, value, tagToStore);
        }
        // else both locked, skip
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
        int idx0 = (int)hashCode & SetMask;
        int idx1 = Sets + ((int)(hashCode >> Way1Shift) & SetMask);
        long hashPart = (hashCode >> HashShift) & HashMask;

        SetCore(in key, value, idx0, idx1, hashPart);
    }

    /// <summary>
    /// Attempts a CAS-guarded write to a single entry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
