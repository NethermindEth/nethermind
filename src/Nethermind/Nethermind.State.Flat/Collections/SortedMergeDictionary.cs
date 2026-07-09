// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// An immutable, build-once dictionary optimized for k-way merging of compacted snapshot content.
/// Entries live in a single array kept sorted by key; a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>-style
/// bucket index is built once on finalization, so lookups are O(1) and enumeration is already in key order
/// (no separate sort needed at persist time).
/// </summary>
/// <remarks>
/// There is no public setter: instances are produced either by <see cref="FromUnsorted"/> (convert a standard
/// dictionary) or <see cref="Merge"/> (loser-tree merge of already-sorted inputs). The bucket array uses a
/// power-of-two size with open chaining identical in shape to the BCL dictionary (a <c>buckets</c> array of
/// 1-based head indices and a <c>Next</c> link per entry).
/// </remarks>
public sealed class SortedMergeDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : IEquatable<TKey>
{
    private struct Entry
    {
        public uint HashCode;
        public int Next;   // 0-based index of the next entry in the bucket chain; -1 terminates.
        public TKey Key;
        public TValue Value;
    }

    private readonly Entry[] _entries;
    private readonly int[] _buckets;
    private readonly int _count;
    private readonly uint _bucketMask;

    private SortedMergeDictionary(Entry[] entries, int count)
    {
        _entries = entries;
        _count = count;

        int size = BucketSize(count);
        _bucketMask = (uint)(size - 1);
        _buckets = new int[size];

        // Iterate the sorted entries once, sizing done, then thread each into its bucket chain.
        for (int i = 0; i < count; i++)
        {
            ref Entry entry = ref entries[i];
            ref int bucket = ref _buckets[entry.HashCode & _bucketMask];
            entry.Next = bucket - 1;
            bucket = i + 1;
        }
    }

    public int Count => _count;

    public bool TryGetValue(TKey key, out TValue value)
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

        value = default!;
        return false;
    }

    /// <summary>Builds a dictionary from an unsorted standard dictionary by sorting its entries once.</summary>
    public static SortedMergeDictionary<TKey, TValue> FromUnsorted(
        IReadOnlyCollection<KeyValuePair<TKey, TValue>> source, IComparer<TKey> keyComparer)
    {
        int count = source.Count;
        Entry[] entries = new Entry[count];
        int i = 0;
        foreach (KeyValuePair<TKey, TValue> kv in source)
        {
            entries[i++] = new Entry { HashCode = (uint)kv.Key.GetHashCode(), Key = kv.Key, Value = kv.Value };
        }

        Array.Sort(entries, 0, count, new EntryKeyComparer(keyComparer));
        return new SortedMergeDictionary<TKey, TValue>(entries, count);
    }

    /// <summary>
    /// Merges already-sorted inputs into a single sorted dictionary using a loser tree. Sources are in ascending
    /// priority order (<paramref name="sources"/><c>[0]</c> is the oldest); when several sources hold the same
    /// key the value from the highest-index (newest) source wins and all of them are consumed.
    /// </summary>
    public static SortedMergeDictionary<TKey, TValue> Merge(
        ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources, IComparer<TKey> keyComparer)
    {
        if (sources.Length == 0) return new SortedMergeDictionary<TKey, TValue>([], 0);
        if (sources.Length == 1) return sources[0];

        int total = 0;
        foreach (SortedMergeDictionary<TKey, TValue> source in sources) total += source._count;
        Entry[] output = new Entry[total];

        LoserTree tree = new(sources, keyComparer);
        int outCount = 0;
        while (tree.TryNext(out Entry winner))
        {
            output[outCount++] = winner;
        }

        return new SortedMergeDictionary<TKey, TValue>(output, outCount);
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

        public bool TryNext(out Entry result)
        {
            int winner = _tree[0];
            if (winner == _k || _position[winner] >= _sources[winner]._count)
            {
                result = default;
                return false;
            }

            ref Entry head = ref _sources[winner]._entries[_position[winner]];
            TKey key = head.Key;
            result = head;

            // Consume every source whose current head has this key; the highest-index source wins the value.
            while (true)
            {
                int current = _tree[0];
                if (current == _k || _position[current] >= _sources[current]._count) break;

                ref Entry currentHead = ref _sources[current]._entries[_position[current]];
                if (_keyComparer.Compare(currentHead.Key, key) != 0) break;

                // Sources are visited in ascending index order for equal keys, so the last one seen is newest.
                result = currentHead;
                _position[current]++;
                Adjust(current);
            }

            return true;
        }

        private void Adjust(int s)
        {
            for (int parent = (s + _k) >> 1; parent > 0; parent >>= 1)
            {
                if (Loses(s, _tree[parent]))
                {
                    (s, _tree[parent]) = (_tree[parent], s);
                }
            }
            _tree[0] = s;
        }

        // True when the head of run a should lose (rank higher) than the head of run b.
        private readonly bool Loses(int a, int b)
        {
            int cmp = CompareHeads(a, b);
            return cmp > 0;
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
