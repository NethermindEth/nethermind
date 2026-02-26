// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.State;

/// <summary>
/// A high-performance one-way set-associative cache using a single cache-line-aligned struct array.
/// Each entry occupies exactly 64 bytes (one cache line), eliminating false sharing between entries.
/// Uses seqlock pattern for lock-free reads and epoch-based O(1) Clear().
/// </summary>
/// <typeparam name="TKey">The key type (must be a struct implementing IEquatable and IHash64)</typeparam>
/// <typeparam name="TValue">The value type (must be a reference type)</typeparam>
public sealed class PreWarmCache<TKey, TValue>
    where TKey : struct, IEquatable<TKey>, IHash64
    where TValue : class?
{
    /// <summary>
    /// Number of cache entries. Must be a power of 2 for mask operations.
    /// 32768 entries × 64 bytes = 2 MB memory footprint.
    /// </summary>
    private const int Count = 1 << 15; // 32768

    private const int BucketMask = Count - 1;

    // 64-bit layout: [Lock:1][Epoch:26][Hash:36][Occupied:1]
    // - Lock (bit 63): Set during writes to signal readers to retry
    // - Epoch (bits 37-62): 26 bits = 67M clears before wrap (~25 years at 1 block/12s)
    // - Hash (bits 1-36): 36 bits for hash signature (+ 15 bucket bits = 51 bits collision resistance)
    // - Occupied (bit 0): Set when slot contains valid data
    private const long LockMarker = unchecked((long)0x8000_0000_0000_0000); // bit 63
    private const int EpochShift = 37;
    private const long EpochMask = 0x7FFF_FFE0_0000_0000; // bits 37-62 (26 bits)
    private const long HashMask = 0x0000_001F_FFFF_FFFE;  // bits 1-36 (36 bits)
    private const long OccupiedBit = 1L;                   // bit 0

    /// <summary>
    /// Cache entry struct - exactly 64 bytes for cache line alignment.
    /// Layout: [HashEpochLock:8][Key:48][Value:8] = 64 bytes
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public long HashEpochLock;  // 8 bytes: lock + epoch + hash signature + occupied
        public TKey Key;            // 48 bytes for StorageCell
        public TValue? Value;       // 8 bytes (reference)
    }

    /// <summary>
    /// Single array of entries - each entry is one cache line.
    /// This eliminates false sharing and reduces cache misses from 3 to 1 per lookup.
    /// </summary>
    private readonly Entry[] _entries;

    /// <summary>
    /// Current epoch counter. Incremented on Clear() to invalidate all entries.
    /// </summary>
    private long _epoch;

    /// <summary>
    /// Pre-computed shifted epoch: (_epoch &lt;&lt; EpochShift) &amp; EpochMask.
    /// Updated atomically in Clear() to avoid shift/mask in hot path.
    /// </summary>
    private long _shiftedEpoch;

    public PreWarmCache()
    {
        _entries = new Entry[Count];
        _epoch = 0;
        _shiftedEpoch = 0;
    }

    /// <summary>
    /// Tries to get a value from the cache using seqlock pattern (lock-free reads).
    /// </summary>
    /// <param name="key">The key to look up</param>
    /// <param name="value">The value if found</param>
    /// <returns>True if the key was found and value is valid</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(in TKey key, out TValue? value)
    {
        long hashCode = key.GetHashCode64();
        int index = (int)hashCode & BucketMask;

        // Pre-computed shifted epoch - avoids shift/mask in hot path
        long currentEpoch = Volatile.Read(ref _shiftedEpoch);
        // Hash signature: bits 15-50 of 64-bit hashCode in bits 1-36 (bucket uses bits 0-14)
        // Full 51-bit collision resistance: 15 bucket + 36 signature
        long hashSignature = ((hashCode >> 14) & HashMask) | OccupiedBit;
        // Expected value: current epoch + hash signature (no lock bit)
        long expected = currentEpoch | hashSignature;

        ref Entry entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), index);

        // Seqlock pattern: read sequence, speculatively read data, verify sequence unchanged
        long seq1 = Volatile.Read(ref entry.HashEpochLock);

        // Early exit: epoch/hash mismatch or write in progress
        // Since expected never has LockMarker set, seq1 != expected catches both cases
        if (seq1 != expected)
        {
            value = default;
            return false;
        }

        // Speculative reads - entry is in same cache line, already fetched
        TKey storedKey = entry.Key;
        TValue? storedValue = entry.Value;

        // On x64: loads are never reordered with other loads - Volatile.Read provides acquire semantics
        // Re-read sequence - if changed, a write occurred during our reads
        long seq2 = Volatile.Read(ref entry.HashEpochLock);

        // If sequence changed, our reads may be torn - bail out
        if (seq1 != seq2)
        {
            value = default;
            return false;
        }

        // Safe to compare - we have a consistent snapshot
        if (storedKey.Equals(key))
        {
            value = storedValue;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Gets a value from the cache, or adds it using the factory if not present.
    /// </summary>
    /// <param name="key">The key to look up or add</param>
    /// <param name="valueFactory">Factory to create value if not present</param>
    /// <returns>The cached or newly created value</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? GetOrAdd(in TKey key, Func<TKey, TValue?> valueFactory)
    {
        if (TryGetValue(in key, out TValue? value))
        {
            return value;
        }
        value = valueFactory(key);
        Set(in key, value);
        return value;
    }

    /// <summary>
    /// Sets a key-value pair in the cache.
    /// On lock contention, the operation is silently skipped.
    /// </summary>
    /// <param name="key">The key to set</param>
    /// <param name="value">The value to set</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(in TKey key, TValue? value)
    {
        long hashCode = key.GetHashCode64();
        int index = (int)hashCode & BucketMask;

        // Pre-computed shifted epoch - avoids shift/mask in hot path
        long currentEpoch = Volatile.Read(ref _shiftedEpoch);
        // Hash signature: bits 15-50 of 64-bit hashCode in bits 1-36
        long hashSignature = ((hashCode >> 14) & HashMask) | OccupiedBit;
        // Value to store: current epoch + hash signature (no lock initially)
        long hashToStore = currentEpoch | hashSignature;

        ref Entry entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), index);
        long existing = Volatile.Read(ref entry.HashEpochLock);

        // Skip if exact match already exists (same epoch, same hash signature)
        // This reduces cache line invalidation for hot storage cells
        if ((existing & ~LockMarker) == hashToStore)
        {
            return;
        }

        // Acquire lock via CAS, write data, release lock
        if ((existing & LockMarker) == 0 &&
            Interlocked.CompareExchange(ref entry.HashEpochLock, hashToStore | LockMarker, existing) == existing)
        {
            entry.Key = key;
            entry.Value = value;

            // Release lock (clears lock bit, sets final hash+epoch)
            Volatile.Write(ref entry.HashEpochLock, hashToStore);
        }
    }

    /// <summary>
    /// Clears all cached entries by incrementing the epoch counter.
    /// Entries with stale epochs are treated as empty on subsequent lookups.
    /// </summary>
    /// <remarks>
    /// This is an O(1) operation with zero memory allocation.
    /// Thread-safe: can be called while other operations are in flight.
    /// With 26-bit epoch, supports 67 million clears before wrap.
    /// Stale key/value data remains in arrays but is inaccessible.
    /// </remarks>
    public void Clear()
    {
        long newEpoch = Interlocked.Increment(ref _epoch);
        // Update pre-computed shifted epoch (readers may see brief inconsistency, but seqlock handles it)
        Volatile.Write(ref _shiftedEpoch, (newEpoch << EpochShift) & EpochMask);
    }
}
