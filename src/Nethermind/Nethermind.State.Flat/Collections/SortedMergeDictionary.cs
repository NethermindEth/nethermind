// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// Build-once, read-only dictionary for k-way merging compacted snapshot content: entries kept sorted by key
/// with a BCL-dictionary-style bucket index, so lookups are O(1) and enumeration is in key order. Backing arrays
/// are pooled and reused across builds via <see cref="NoResizeClear"/>.
/// </summary>
internal sealed class SortedMergeDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IDisposable
    where TKey : IEquatable<TKey>
{
    private struct Entry
    {
        public uint HashCode;
        public int Next;
        public TKey Key;
        public TValue Value;
    }

    private Entry[] _entries = [];
    private int[] _buckets = [];
    private int _count;
    private int _bucketCount;
    private uint _bucketMask;

    public int Count => _count;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_count != 0)
        {
            uint hashCode = (uint)key.GetHashCode();
            int i = _buckets[hashCode & _bucketMask] - 1;
            while ((uint)i < (uint)_count)
            {
                ref Entry entry = ref _entries[i];
                if (entry.HashCode == hashCode && entry.Key.Equals(key))
                {
                    value = entry.Value;
                    return true;
                }
                i = entry.Next;
            }
        }

        value = default!;
        return false;
    }

    public void BuildFromUnsorted(IReadOnlyCollection<KeyValuePair<TKey, TValue>> source, IComparer<TKey> keyComparer)
    {
        int count = source.Count;
        EnsureEntryCapacity(count);
        int i = 0;
        foreach (KeyValuePair<TKey, TValue> kv in source)
        {
            _entries[i++] = new Entry { HashCode = (uint)kv.Key.GetHashCode(), Key = kv.Key, Value = kv.Value };
        }

        Array.Sort(_entries, 0, count, new EntryKeyComparer(keyComparer));
        _count = count;
        BuildBuckets();
    }

    /// <summary>
    /// Merges already-sorted inputs (ascending priority; the highest-index source wins on equal keys). When
    /// <paramref name="keep"/> is supplied, entries it rejects are dropped and keys with no survivor are omitted.
    /// </summary>
    public void BuildFromMerge(
        ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources,
        IComparer<TKey> keyComparer,
        Func<int, TKey, bool>? keep = null)
    {
        if (sources.Length == 0)
        {
            _count = 0;
            BuildBuckets();
            return;
        }

        int total = 0;
        foreach (SortedMergeDictionary<TKey, TValue> source in sources) total += source._count;
        EnsureEntryCapacity(total);

        LoserTree tree = new(sources, keyComparer);
        int count = 0;
        while (tree.TryNext(keep, out Entry chosen))
        {
            _entries[count++] = chosen;
        }

        _count = count;
        BuildBuckets();
    }

    public void NoResizeClear()
    {
        if (_count > 0) Array.Clear(_entries, 0, _count);
        if (_bucketCount > 0) Array.Clear(_buckets, 0, _bucketCount);
        _count = 0;
        _bucketCount = 0;
    }

    public void Dispose()
    {
        if (_entries.Length > 0)
        {
            ArrayPool<Entry>.Shared.Return(_entries, clearArray: true);
            _entries = [];
        }
        if (_buckets.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_buckets);
            _buckets = [];
        }
        _count = 0;
        _bucketCount = 0;
    }

    private void EnsureEntryCapacity(int count)
    {
        if (_entries.Length >= count) return;

        Entry[] old = _entries;
        _entries = ArrayPool<Entry>.Shared.Rent(count);
        Array.Clear(_entries); // rented arrays aren't zeroed; keep the tail clear so it never pins stale objects
        if (old.Length > 0) ArrayPool<Entry>.Shared.Return(old, clearArray: true);
    }

    private void BuildBuckets()
    {
        int size = BucketSize(_count);
        if (_buckets.Length < size)
        {
            int[] old = _buckets;
            _buckets = ArrayPool<int>.Shared.Rent(size);
            if (old.Length > 0) ArrayPool<int>.Shared.Return(old);
        }

        _bucketCount = size;
        _bucketMask = (uint)(size - 1);
        Array.Clear(_buckets, 0, size);

        // buckets store a 1-based entry index (0 == empty); Next is the 0-based previous chain head, -1 at the end.
        for (int i = 0; i < _count; i++)
        {
            ref Entry entry = ref _entries[i];
            ref int bucket = ref _buckets[entry.HashCode & _bucketMask];
            entry.Next = bucket - 1;
            bucket = i + 1;
        }
    }

    public static SortedMergeDictionary<TKey, TValue> FromUnsorted(
        IReadOnlyCollection<KeyValuePair<TKey, TValue>> source, IComparer<TKey> keyComparer)
    {
        SortedMergeDictionary<TKey, TValue> dictionary = new();
        dictionary.BuildFromUnsorted(source, keyComparer);
        return dictionary;
    }

    public static SortedMergeDictionary<TKey, TValue> Merge(
        ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources,
        IComparer<TKey> keyComparer,
        Func<int, TKey, bool>? keep = null)
    {
        SortedMergeDictionary<TKey, TValue> dictionary = new();
        dictionary.BuildFromMerge(sources, keyComparer, keep);
        return dictionary;
    }

    // Target load factor: size buckets so at most ~70% are occupied, keeping chains short.
    private const double MaxLoadFactor = 0.7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketSize(int count) =>
        count == 0 ? 1 : (int)BitOperations.RoundUpToPowerOf2((uint)(count / MaxLoadFactor) + 1);

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(SortedMergeDictionary<TKey, TValue> dictionary) : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private int _index = -1;

        public readonly KeyValuePair<TKey, TValue> Current
        {
            get
            {
                ref Entry entry = ref dictionary._entries[_index];
                return new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
            }
        }

        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < dictionary._count;
        public void Reset() => _index = -1;
        public readonly void Dispose() { }
    }

    private sealed class EntryKeyComparer(IComparer<TKey> keyComparer) : IComparer<Entry>
    {
        public int Compare(Entry x, Entry y) => keyComparer.Compare(x.Key, y.Key);
    }

    /// <summary>
    /// Tournament (loser) tree over the k sorted runs. Each internal node holds the loser of a match; the overall
    /// winner (smallest current head) ends up at <c>_tree[0]</c>. <see cref="TryNext"/> reads that winner, advances
    /// its run, and <see cref="Adjust"/> replays only that leaf's path to the root — O(log k) per element rather
    /// than O(k). Leaf index <c>_k</c> is a sentinel: it seeds the tree (smaller than any real head) and marks
    /// exhausted runs (larger than any real head).
    /// </summary>
    private ref struct LoserTree
    {
        private readonly ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> _sources;
        private readonly IComparer<TKey> _keyComparer;
        private readonly int _k;
        private readonly int[] _tree;
        private readonly int[] _position;

        public LoserTree(ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources, IComparer<TKey> keyComparer)
        {
            _sources = sources;
            _keyComparer = keyComparer;
            _k = sources.Length;
            _tree = new int[_k];
            _position = new int[_k];

            for (int i = 0; i < _k; i++) _tree[i] = _k;
            for (int i = _k - 1; i >= 0; i--) Adjust(i);
        }

        // Emits the next distinct key, collapsing equal-keyed heads to the highest-index kept value. Fully
        // filtered keys are skipped, so a returned entry is always a real one.
        public bool TryNext(Func<int, TKey, bool>? keep, out Entry chosen)
        {
            while (true)
            {
                int winner = _tree[0];
                if (winner == _k || _position[winner] >= _sources[winner]._count)
                {
                    chosen = default;
                    return false;
                }

                TKey key = _sources[winner]._entries[_position[winner]].Key;
                bool hasChosen = false;
                chosen = default;

                while (true)
                {
                    int current = _tree[0];
                    if (current == _k || _position[current] >= _sources[current]._count) break;

                    ref Entry currentHead = ref _sources[current]._entries[_position[current]];
                    if (_keyComparer.Compare(currentHead.Key, key) != 0) break;

                    if (keep is null || keep(current, currentHead.Key))
                    {
                        chosen = currentHead;
                        hasChosen = true;
                    }
                    _position[current]++;
                    Adjust(current);
                }

                if (hasChosen) return true;
            }
        }

        private void Adjust(int s)
        {
            for (int parent = (s + _k) >> 1; parent > 0; parent >>= 1)
            {
                if (CompareHeads(s, _tree[parent]) > 0)
                {
                    (s, _tree[parent]) = (_tree[parent], s);
                }
            }
            _tree[0] = s;
        }

        private readonly int CompareHeads(int a, int b)
        {
            if (a == _k) return b == _k ? 0 : -1;
            if (b == _k) return 1;

            bool aExhausted = _position[a] >= _sources[a]._count;
            bool bExhausted = _position[b] >= _sources[b]._count;
            if (aExhausted || bExhausted)
            {
                if (aExhausted && bExhausted) return a.CompareTo(b);
                return aExhausted ? 1 : -1;
            }

            int cmp = _keyComparer.Compare(
                _sources[a]._entries[_position[a]].Key,
                _sources[b]._entries[_position[b]].Key);
            return cmp != 0 ? cmp : a.CompareTo(b);
        }
    }
}
