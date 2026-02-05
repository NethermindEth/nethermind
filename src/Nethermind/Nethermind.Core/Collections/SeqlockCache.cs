// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core.Collections;

/// <summary>
/// A high-performance direct-mapped cache using a single struct array and a seqlock-style header per entry.
/// 
/// Design goals:
/// - Lock-free reads (seqlock pattern) - readers never take locks.
/// - Best-effort writes - writers skip on contention.
/// - O(1) logical Clear() via a global epoch (no per-entry zeroing).
/// - One 64-bit header per entry combines: lock bit, epoch, hash signature, per-entry sequence, occupied.
/// 
/// Header layout (64-bit):
/// [Lock:1][Epoch:26][Hash:28][Seq:8][Occ:1]
/// - Lock (bit 63): set during writes - readers retry/miss
/// - Epoch (bits 37-62): global epoch tag - changes on Clear()
/// - Hash  (bits  9-36): per-bucket hash signature (28 bits)
/// - Seq   (bits  1- 8): per-entry sequence counter (8 bits) - increments on every successful write
/// - Occ   (bit   0): occupied flag - set when slot contains valid data (value may still be null)
/// 
/// Notes on cache-line sizing:
/// - If TKey is 48 bytes and TValue is a reference, Entry tends to be 64 bytes (8 + 48 + 8),
///   which often maps nicely to cache lines.
/// - Managed arrays are not guaranteed to be 64-byte aligned, and generic TKey size is not enforced,
///   so this is "cache-line friendly" rather than a hard guarantee.
/// </summary>
/// <typeparam name="TKey">The key type (struct implementing IHash64bit)</typeparam>
/// <typeparam name="TValue">The value type (reference type, nullable allowed)</typeparam>
public sealed class SeqlockCache<TKey, TValue>
    where TKey : struct, IHash64bit<TKey>
    where TValue : class?
{
    /// <summary>
    /// Number of cache entries. Must be a power of 2 for mask operations.
    /// 32768 entries -> small working set with predictable indexing.
    /// </summary>
    private const int Count = 1 << 15; // 32768
    private const int BucketMask = Count - 1;

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
    // Used to compare an observed header against an expected tag without caring about Seq.
    private const long TagMask = EpochMask | HashMask | OccupiedBit;

    // We take hash signature bits from the 64-bit hash and place them into bits 9-36.
    // With HashShift = 6, bits 9-36 of (hashCode >> 6) correspond to original bits 15-42.
    // Bucket index uses low 15 bits (0-14), so this avoids overlapping the index bits.
    private const int HashShift = 6;

    /// <summary>
    /// Single array of entries. Lookups touch one entry (direct-mapped).
    /// Collisions overwrite the resident entry for that bucket.
    /// </summary>
    private readonly Entry[] _entries;

    /// <summary>
    /// Current epoch counter (unshifted, informational / debugging).
    /// </summary>
    private long _epoch;

    /// <summary>
    /// Pre-shifted epoch tag: (_epoch << EpochShift) & EpochMask.
    /// Readers use this directly to avoid shift/mask in the hot path.
    /// </summary>
    private long _shiftedEpoch;

    public SeqlockCache()
    {
        _entries = new Entry[Count];
        _epoch = 0;
        _shiftedEpoch = 0;
    }

    /// <summary>
    /// Tries to get a value from the cache using a seqlock pattern (lock-free reads).
    /// 
    /// Reader protocol:
    /// - Read header (Volatile) - if locked or tag mismatch, miss.
    /// - Speculatively read Key/Value.
    /// - Re-read header (Volatile) - if changed, miss (write overlap).
    /// - Compare key, return value.
    /// 
    /// Seq field prevents "final == initial" ABA-style acceptance across same-tag rewrites,
    /// except in the extreme case of seq wrapping (8-bit) between the two reads.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(in TKey key, out TValue? value)
    {
        long hashCode = key.GetHashCode64();
        int index = (int)hashCode & BucketMask;

        long epochTag = Volatile.Read(ref _shiftedEpoch);

        // Expected tag (no Lock, no Seq).
        long hashPart = (hashCode >> HashShift) & HashMask;
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), index);

        // First header read (acquire).
        long h1 = Volatile.Read(ref entry.HashEpochSeqLock);

        // Fast reject:
        // - lock held -> masked header includes LockMarker, won't equal expectedTag
        // - tag mismatch -> masked tag bits won't equal expectedTag
        if ((h1 & (TagMask | LockMarker)) != expectedTag)
        {
            value = default;
            return false;
        }

        // Speculative payload reads.
        // Avoid copying large keys unless your TKey forces it - keep it as ref readonly.
        ref readonly TKey storedKey = ref entry.Key;
        TValue? storedValue = entry.Value;

        // Second header read (acquire). If different, payload may be torn.
        long h2 = Volatile.Read(ref entry.HashEpochSeqLock);
        if (h1 != h2)
        {
            value = default;
            return false;
        }

        if (storedKey.Equals(in key))
        {
            value = storedValue;
            return true;
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
    /// Uses the in-key factory overload to avoid large key copies.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? GetOrAdd(in TKey key, ValueFactory valueFactory)
    {
        // Compute hash once, reuse for both TryGet and Set
        long hashCode = key.GetHashCode64();
        int index = (int)hashCode & BucketMask;
        long hashPart = (hashCode >> HashShift) & HashMask;

        if (TryGetValueCore(in key, index, hashPart, out TValue? value))
        {
            return value;
        }

        value = valueFactory(in key);
        SetCore(in key, value, index, hashPart);
        return value;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetValueCore(in TKey key, int index, long hashPart, out TValue? value)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long expectedTag = epochTag | hashPart | OccupiedBit;

        ref Entry entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), index);

        long h1 = Volatile.Read(ref entry.HashEpochSeqLock);

        if ((h1 & (TagMask | LockMarker)) != expectedTag)
        {
            value = default;
            return false;
        }

        ref readonly TKey storedKey = ref entry.Key;
        TValue? storedValue = entry.Value;

        long h2 = Volatile.Read(ref entry.HashEpochSeqLock);
        if (h1 != h2)
        {
            value = default;
            return false;
        }

        if (storedKey.Equals(in key))
        {
            value = storedValue;
            return true;
        }

        value = default;
        return false;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetCore(in TKey key, TValue? value, int index, long hashPart)
    {
        long epochTag = Volatile.Read(ref _shiftedEpoch);
        long tagToStore = epochTag | hashPart | OccupiedBit;

        ref Entry entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), index);

        long existing = Volatile.Read(ref entry.HashEpochSeqLock);

        if (existing < 0)
        {
            return;
        }

        if ((existing & TagMask) == tagToStore && entry.Key.Equals(in key) && ReferenceEquals(entry.Value, value))
        {
            return;
        }

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
    /// Gets a value from the cache, or adds it using the factory if not present.
    /// Note: Func&lt;TKey, TValue?&gt; takes TKey by value - for large TKey this can be a measurable copy.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? GetOrAdd(in TKey key, Func<TKey, TValue?> valueFactory)
    {
        // Compute hash once, reuse for both TryGet and Set
        long hashCode = key.GetHashCode64();
        int index = (int)hashCode & BucketMask;
        long hashPart = (hashCode >> HashShift) & HashMask;

        if (TryGetValueCore(in key, index, hashPart, out TValue? value))
        {
            return value;
        }

        value = valueFactory(key);
        SetCore(in key, value, index, hashPart);
        return value;
    }

    /// <summary>
    /// Sets a key-value pair in the cache.
    /// This is a direct-mapped cache: a hash collision overwrites the existing entry in that slot.
    /// 
    /// Writers are best-effort:
    /// - If the slot is locked or CAS fails, the write is silently skipped.
    /// 
    /// Seqlock writer protocol:
    /// - Read existing header
    /// - CAS header to "locked" and with incremented Seq
    /// - Write Key/Value
    /// - Release by publishing final header with Volatile.Write (clears lock)
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(in TKey key, TValue? value)
    {
        long hashCode = key.GetHashCode64();
        int index = (int)hashCode & BucketMask;

        long epochTag = Volatile.Read(ref _shiftedEpoch);

        long hashPart = (hashCode >> HashShift) & HashMask;
        long tagToStore = epochTag | hashPart | OccupiedBit; // no Seq, no Lock

        ref Entry entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), index);

        long existing = Volatile.Read(ref entry.HashEpochSeqLock);

        // Skip if locked (best-effort write).
        if (existing < 0) // sign bit == LockMarker
        {
            return;
        }

        // Optional "avoid invalidation" fast-path:
        // If tag matches and key matches and value already matches, do nothing.
        // Requires unlocked (guaranteed by existing < 0 check).
        if ((existing & TagMask) == tagToStore && entry.Key.Equals(in key) && ReferenceEquals(entry.Value, value))
        {
            return;
        }

        // Bump Seq for every successful write so readers can detect same-tag rewrites.
        long newSeq = ((existing & SeqMask) + SeqInc) & SeqMask;

        long lockedHeader = tagToStore | newSeq | LockMarker;

        // Acquire via CAS.
        if (Interlocked.CompareExchange(ref entry.HashEpochSeqLock, lockedHeader, existing) != existing)
        {
            return;
        }

        // Write payload while locked.
        entry.Key = key;
        entry.Value = value;

        // Release - publish final header (clears lock, keeps seq).
        Volatile.Write(ref entry.HashEpochSeqLock, tagToStore | newSeq);
    }

    /// <summary>
    /// Clears all cached entries by incrementing the global epoch tag (O(1)).
    /// Entries with stale epochs are treated as empty on subsequent lookups.
    /// 
    /// Thread-safe: can be called while other operations are in flight.
    /// 
    /// Note:
    /// - This is a logical clear. Stale key/value data remains in the array until overwritten.
    ///   Values are still strongly referenced (up to Count entries) until replaced.
    /// </summary>
    public void Clear()
    {
        // Atomically increment the epoch tag so readers never observe a partially updated state.
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
