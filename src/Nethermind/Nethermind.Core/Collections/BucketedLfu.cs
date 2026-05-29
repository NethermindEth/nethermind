// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// O(1) bucketed Least-Frequently-Used cache. Tracks access frequency per key.
/// Used by the sparse trie for cross-block hot path retention (M3).
/// <remarks>
/// Port of Reth's BucketedLfu. Frequencies are bytes (1-255). Touch increments frequency.
/// DecayAndEvict trims to capacity by removing the lowest-frequency entries.
/// </remarks>
/// </summary>
public sealed class BucketedLfu<TKey> where TKey : notnull
{
    private const byte MaxFrequency = 255;
    private const byte MinFrequency = 1;

    private readonly Dictionary<TKey, EntryMeta> _entries;
    private readonly List<TKey>[] _buckets;
    private readonly object _gate = new();
    private int _minFreq = MinFrequency;
    private int _capacity;

    private struct EntryMeta
    {
        public byte Freq;
        public int Pos; // index within the bucket's list
    }

    public BucketedLfu(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        // Cap initial dictionary capacity so callers passing int.MaxValue (effectively "no
        // eviction") don't trigger the Dictionary ctor to allocate a 2 billion-entry table.
        // Dictionary grows on insertion; starting small is fine for the LFU usage pattern.
        int initialCap = _capacity > 16_384 ? 16_384 : _capacity;
        _entries = new Dictionary<TKey, EntryMeta>(initialCap);
        _buckets = new List<TKey>[MaxFrequency + 1];
        for (int i = 0; i <= MaxFrequency; i++)
            _buckets[i] = [];
    }

    public int Count { get { lock (_gate) return _entries.Count; } }
    public int Capacity => _capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Touch(TKey key)
    {
        // Concurrent block processing touches the LFU from many threads (per-contract
        // storage compute runs in parallel via PersistentStorageProvider.UpdateRootHashesMultiThread).
        // Dictionary + List<T> are not thread-safe; without the lock the bucket positions
        // diverge from the entry meta and RemoveFromBucket throws IndexOutOfRange. A single
        // monitor lock is fine here because Touch is O(1) and the hot path inside is small.
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out EntryMeta meta))
            {
                // Existing key — increment frequency
                RemoveFromBucket(key, meta.Freq, meta.Pos);
                byte newFreq = meta.Freq < MaxFrequency ? (byte)(meta.Freq + 1) : MaxFrequency;
                int newPos = _buckets[newFreq].Count;
                _buckets[newFreq].Add(key);
                _entries[key] = new EntryMeta { Freq = newFreq, Pos = newPos };

                if (_buckets[meta.Freq].Count == 0 && meta.Freq == _minFreq)
                    _minFreq = newFreq;
            }
            else
            {
                if (_entries.Count >= _capacity)
                    EvictOne();

                int pos = _buckets[MinFrequency].Count;
                _buckets[MinFrequency].Add(key);
                _entries[key] = new EntryMeta { Freq = MinFrequency, Pos = pos };
                _minFreq = MinFrequency;
            }
        }
    }

    /// <summary>
    /// Evicts entries down to the given capacity. Returns the evicted keys.
    /// </summary>
    public List<TKey> DecayAndEvict(int newCapacity)
    {
        lock (_gate)
        {
            _capacity = Math.Max(1, newCapacity);
            List<TKey> evicted = [];
            while (_entries.Count > _capacity)
            {
                TKey? victim = EvictOne();
                if (victim is not null) evicted.Add(victim);
                else break;
            }
            return evicted;
        }
    }

    /// <summary>Returns a snapshot of currently retained keys (allocates a copy under the lock
    /// so callers can iterate without races against concurrent Touch calls).</summary>
    public IReadOnlyCollection<TKey> RetainedKeys
    {
        get { lock (_gate) { return [.. _entries.Keys]; } }
    }

    private TKey? EvictOne()
    {
        for (int f = _minFreq; f <= MaxFrequency; f++)
        {
            List<TKey> bucket = _buckets[f];
            if (bucket.Count == 0) continue;

            // Pop the last entry (O(1))
            int lastIdx = bucket.Count - 1;
            TKey victim = bucket[lastIdx];
            bucket.RemoveAt(lastIdx);
            _entries.Remove(victim);

            if (bucket.Count == 0 && f == _minFreq)
                _minFreq = FindMinFreq(f);

            return victim;
        }
        return default;
    }

    private void RemoveFromBucket(TKey key, byte freq, int pos)
    {
        List<TKey> bucket = _buckets[freq];
        int lastIdx = bucket.Count - 1;

        if (pos != lastIdx)
        {
            // Swap with last element
            TKey swapped = bucket[lastIdx];
            bucket[pos] = swapped;
            EntryMeta swappedMeta = _entries[swapped];
            swappedMeta.Pos = pos;
            _entries[swapped] = swappedMeta;
        }

        bucket.RemoveAt(lastIdx);
    }

    private int FindMinFreq(int startFrom)
    {
        for (int f = startFrom; f <= MaxFrequency; f++)
        {
            if (_buckets[f].Count > 0) return f;
        }
        return MinFrequency;
    }
}
