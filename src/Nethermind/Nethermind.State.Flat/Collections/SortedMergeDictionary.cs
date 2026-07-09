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
/// A build-once, then read-only dictionary optimized for k-way merging of compacted snapshot content.
/// Entries live in a single array kept sorted by key; a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>-style
/// bucket index is built alongside, so lookups are O(1) and enumeration is already in key order (no separate
/// sort needed at persist time).
/// </summary>
/// <remarks>
/// The backing arrays are rented from <see cref="ArrayPool{T}"/> and reused: rebuild it in place with
/// <see cref="BuildFromMerge"/> (loser-tree merge of already-sorted inputs) or <see cref="BuildFromUnsorted"/>
/// (sort a standard dictionary), and empty it with <see cref="NoResizeClear"/> to keep the arrays for the next
/// build. This lets a pooled content hold warm dictionaries across compactions instead of reallocating. The
/// bucket array uses a power-of-two size with open chaining identical in shape to the BCL dictionary.
/// </remarks>
public sealed class SortedMergeDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IDisposable
    where TKey : IEquatable<TKey>
{
    private struct Entry
    {
        public uint HashCode;
        public int Next;   // 0-based index of the next entry in the bucket chain; -1 terminates.
        public TKey Key;
        public TValue Value;
    }

    private Entry[] _entries = [];
    private int[] _buckets = [];
    private int _count;
    private int _bucketCount;   // active prefix of _buckets, a power of two <= _buckets.Length
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

    /// <summary>Rebuilds this dictionary in place from an unsorted standard dictionary by sorting its entries once.</summary>
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
    /// Rebuilds this dictionary in place by merging already-sorted inputs with a loser tree. Sources are in
    /// ascending priority order (<paramref name="sources"/><c>[0]</c> is the oldest); when several sources hold
    /// the same key the value from the highest-index (newest) source wins and all of them are consumed. When
    /// <paramref name="keep"/> is supplied it is called with each entry's source index and key; entries for
    /// which it returns <c>false</c> are dropped, and a key with no surviving entry is not emitted.
    /// </summary>
    public void BuildFromMerge(
        ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources,
        IComparer<TKey> keyComparer,
        Func<int, TKey, bool>? keep = null)
    {
        int total = 0;
        foreach (SortedMergeDictionary<TKey, TValue> source in sources) total += source._count;
        EnsureEntryCapacity(total);

        LoserTree tree = new(sources, keyComparer);
        int count = 0;
        while (tree.TryNextRun(keep, out Entry chosen, out bool hasChosen))
        {
            if (hasChosen) _entries[count++] = chosen;
        }

        _count = count;
        BuildBuckets();
    }

    /// <summary>Empties the dictionary while keeping its rented arrays for the next build.</summary>
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
        // Rented arrays are not zeroed; clear the tail so entries above the live count never pin stale objects.
        Array.Clear(_entries);
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

        for (int i = 0; i < _count; i++)
        {
            ref Entry entry = ref _entries[i];
            ref int bucket = ref _buckets[entry.HashCode & _bucketMask];
            entry.Next = bucket - 1;
            bucket = i + 1;
        }
    }

    /// <summary>Builds a fresh dictionary from an unsorted standard dictionary (convenience for one-off use).</summary>
    public static SortedMergeDictionary<TKey, TValue> FromUnsorted(
        IReadOnlyCollection<KeyValuePair<TKey, TValue>> source, IComparer<TKey> keyComparer)
    {
        SortedMergeDictionary<TKey, TValue> dictionary = new();
        dictionary.BuildFromUnsorted(source, keyComparer);
        return dictionary;
    }

    /// <summary>Builds a fresh dictionary by merging already-sorted inputs (convenience for one-off use).</summary>
    public static SortedMergeDictionary<TKey, TValue> Merge(
        ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources,
        IComparer<TKey> keyComparer,
        Func<int, TKey, bool>? keep = null)
    {
        SortedMergeDictionary<TKey, TValue> dictionary = new();
        dictionary.BuildFromMerge(sources, keyComparer, keep);
        return dictionary;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketSize(int count)
    {
        if (count <= 1) return 1;
        return (int)BitOperations.RoundUpToPowerOf2((uint)count);
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Enumerates entries in ascending key order.</summary>
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
    /// A loser tree (tournament tree) over the sorted source runs. Emits merged entries in ascending key order,
    /// collapsing duplicate keys to the highest-priority source's value.
    /// </summary>
    private ref struct LoserTree
    {
        private readonly ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> _sources;
        private readonly IComparer<TKey> _keyComparer;
        private readonly int _k;
        private readonly int[] _tree;   // _tree[0] is the winner leaf; _tree[1..] hold losers.
        private readonly int[] _position;

        public LoserTree(ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources, IComparer<TKey> keyComparer)
        {
            _sources = sources;
            _keyComparer = keyComparer;
            _k = sources.Length;
            _tree = new int[_k];
            _position = new int[_k];

            // Initialize: every internal node holds the MIN sentinel (leaf index _k), then play each real leaf
            // in so the smaller of each match is carried up and the larger stored as the node's loser.
            for (int i = 0; i < _k; i++) _tree[i] = _k;
            for (int i = _k - 1; i >= 0; i--) Adjust(i);
        }

        public bool TryNextRun(Func<int, TKey, bool>? keep, out Entry chosen, out bool hasChosen)
        {
            int winner = _tree[0];
            if (winner == _k || _position[winner] >= _sources[winner]._count)
            {
                chosen = default;
                hasChosen = false;
                return false;
            }

            TKey key = _sources[winner]._entries[_position[winner]].Key;
            chosen = default;
            hasChosen = false;

            // Consume every source whose current head has this key; the highest-index kept entry wins the value.
            while (true)
            {
                int current = _tree[0];
                if (current == _k || _position[current] >= _sources[current]._count) break;

                ref Entry currentHead = ref _sources[current]._entries[_position[current]];
                if (_keyComparer.Compare(currentHead.Key, key) != 0) break;

                // Sources are visited in ascending index order for equal keys, so the last kept one is newest.
                if (keep is null || keep(current, currentHead.Key))
                {
                    chosen = currentHead;
                    hasChosen = true;
                }
                _position[current]++;
                Adjust(current);
            }

            return true;
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
            // The MIN sentinel (leaf index _k) is smaller than every real head; it only exists during init.
            if (a == _k) return b == _k ? 0 : -1;
            if (b == _k) return 1;

            bool aExhausted = _position[a] >= _sources[a]._count;
            bool bExhausted = _position[b] >= _sources[b]._count;
            if (aExhausted || bExhausted)
            {
                if (aExhausted && bExhausted) return a.CompareTo(b);
                return aExhausted ? 1 : -1; // an exhausted run ranks after every live one
            }

            int cmp = _keyComparer.Compare(
                _sources[a]._entries[_position[a]].Key,
                _sources[b]._entries[_position[b]].Key);
            return cmp != 0 ? cmp : a.CompareTo(b); // equal keys: lower source index is emitted first
        }
    }
}
