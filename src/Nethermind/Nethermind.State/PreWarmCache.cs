// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.State;

/// <summary>
/// A high-performance one-way set-associative cache similar to <see cref="Nethermind.Core.Crypto.KeccakCache"/>.
/// Uses parallel arrays for hash codes, keys, and values to avoid boxing.
/// Supports reference type values and O(1) Clear() via epoch-based invalidation.
/// </summary>
/// <typeparam name="TKey">The key type (must be a struct implementing IEquatable)</typeparam>
/// <typeparam name="TValue">The value type (must be a reference type)</typeparam>
public sealed class PreWarmCache<TKey, TValue>
    where TKey : struct, IEquatable<TKey>
    where TValue : class?
{
    /// <summary>
    /// Number of cache entries. Must be a power of 2 for mask operations.
    /// </summary>
    private const int Count = 1 << 14; // 16384

    private const int BucketMask = Count - 1;

    // Bit layout: [Lock:1][Epoch:12][Hash:18][Occupied:1]
    // Collision detection uses 32 bits: 14 bits in bucket index + 18 bits in hash signature
    private const int LockMarker = unchecked((int)0x8000_0000); // bit 31
    private const int EpochShift = 19;
    private const int EpochMask = 0x7FF8_0000; // bits 19-30 (12 bits, 4096 epochs)
    private const int HashMask = 0x0007_FFFE;  // bits 1-18 (18 bits), captures hashCode bits 15-31
    private const int OccupiedBit = 1;         // bit 0

    /// <summary>
    /// Array storing hash codes with lock marker, epoch, and hash signature.
    /// Bit layout: [Lock:1][Epoch:12][Hash:18][Occupied:1]
    /// A value of 0 means the slot is empty or has stale epoch.
    /// </summary>
    private readonly int[] _hashes;

    /// <summary>
    /// Array of keys. Only valid when corresponding _hashes entry matches current epoch.
    /// </summary>
    private readonly TKey[] _keys;

    /// <summary>
    /// Array of values corresponding to keys.
    /// </summary>
    private readonly TValue?[] _values;

    /// <summary>
    /// Current epoch counter. Incremented on Clear() to invalidate all entries.
    /// </summary>
    private int _epoch;

    public PreWarmCache()
    {
        _hashes = new int[Count];
        _keys = new TKey[Count];
        _values = new TValue?[Count];
        _epoch = 0;
    }

    /// <summary>
    /// Tries to get a value from the cache.
    /// </summary>
    /// <param name="key">The key to look up</param>
    /// <param name="value">The value if found</param>
    /// <returns>True if the key was found and value is valid</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(in TKey key, out TValue? value)
    {
        int hashCode = key.GetHashCode();
        int index = hashCode & BucketMask;

        // Current epoch shifted into position
        int currentEpoch = (Volatile.Read(ref _epoch) << EpochShift) & EpochMask;
        // Hash signature: bits 15-31 of hashCode in bits 1-18 (bucket uses bits 0-13)
        int hashSignature = ((hashCode >> 14) & HashMask) | OccupiedBit;
        // Expected value: current epoch + hash signature (no lock bit)
        int expected = currentEpoch | hashSignature;

        ref int hashSlot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_hashes), index);
        int existing = Volatile.Read(ref hashSlot);

        // Check: epoch matches, hash matches, not locked
        if ((existing & ~LockMarker) == expected &&
            (existing & LockMarker) == 0 &&
            Interlocked.CompareExchange(ref hashSlot, existing | LockMarker, existing) == existing)
        {
            TKey storedKey = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_keys), index);
            TValue? storedValue = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), index);

            // Release lock
            Volatile.Write(ref hashSlot, existing);

            if (storedKey.Equals(key))
            {
                value = storedValue;
                return true;
            }
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
        TryAdd(in key, value);
        return value;
    }

    /// <summary>
    /// Indexer for setting values. Getting via indexer throws if key not found.
    /// </summary>
    public TValue? this[in TKey key]
    {
        get
        {
            if (TryGetValue(in key, out TValue? value))
            {
                return value;
            }
            throw new System.Collections.Generic.KeyNotFoundException();
        }
        set => TryAdd(in key, value);
    }

    /// <summary>
    /// Tries to add a key-value pair to the cache.
    /// On collision or lock contention, the operation is silently skipped.
    /// </summary>
    /// <param name="key">The key to add</param>
    /// <param name="value">The value to add</param>
    /// <returns>True if the value was added, false if skipped due to collision or lock contention</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(in TKey key, TValue? value)
    {
        int hashCode = key.GetHashCode();
        int index = hashCode & BucketMask;

        // Current epoch shifted into position
        int currentEpoch = (Volatile.Read(ref _epoch) << EpochShift) & EpochMask;
        // Hash signature: bits 15-31 of hashCode in bits 1-18 (bucket uses bits 0-13)
        int hashSignature = ((hashCode >> 14) & HashMask) | OccupiedBit;
        // Value to store: current epoch + hash signature (no lock initially)
        int hashToStore = currentEpoch | hashSignature;

        ref int hashSlot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_hashes), index);
        int existing = Volatile.Read(ref hashSlot);

        // Skip if exact match already exists (same epoch, same hash signature)
        // This reduces cache line invalidation for hot storage cells
        if ((existing & ~LockMarker) == hashToStore)
        {
            return true;
        }

        // Can add if: slot is empty (0), OR has stale epoch, OR different key (overwrite)
        // Just need to acquire lock
        if ((existing & LockMarker) == 0 &&
            Interlocked.CompareExchange(ref hashSlot, hashToStore | LockMarker, existing) == existing)
        {
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_keys), index) = key;
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), index) = value;

            // Release lock
            Volatile.Write(ref hashSlot, hashToStore);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Clears all cached entries by incrementing the epoch counter.
    /// Entries with stale epochs are treated as empty on subsequent lookups.
    /// </summary>
    /// <remarks>
    /// This is an O(1) operation with zero memory allocation.
    /// Thread-safe: can be called while other operations are in flight.
    /// Stale key/value data remains in arrays but is inaccessible.
    /// </remarks>
    public void Clear()
    {
        Interlocked.Increment(ref _epoch);
    }
}
