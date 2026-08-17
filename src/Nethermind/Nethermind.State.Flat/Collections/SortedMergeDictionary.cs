// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// Build-once, read-only dictionary for k-way merging compacted snapshot content: entries kept sorted by key
/// with a BCL-dictionary-style bucket index, so lookups are O(1) and enumeration is in key order. Backing arrays
/// are pooled and reused across builds via <see cref="NoResizeClear"/>.
/// </summary>
/// <remarks>
/// Buckets store <c>entryIndex + 1 + _bucketSalt</c>; the salt grows every build, so stale slots decode out of
/// range and read as empty without per-build clearing. Entry arrays pool through a type private to this class
/// and are always returned all-default (<see cref="_entriesDirty"/>), so renting them needs no clearing either.
/// </remarks>
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
    /// <summary>High-water mark of entries written since <see cref="_entries"/> was last all-default.</summary>
    private int _entriesDirty;
    private uint _bucketMask;
    /// <summary>Total entries stamped into the current bucket array; each build stamps slots in <c>(salt, salt + count]</c>.</summary>
    private int _bucketSalt;
    /// <summary><c>-1 - salt</c> of the last build: slot + bias is the entry index, negative when the slot is stale.</summary>
    private int _bucketBias;
    /// <summary>Zeroed prefix of <see cref="_buckets"/>; Rent may return extra length still holding foreign garbage.</summary>
    private int _bucketsCleared;

    public int Count => _count;

    public bool TryGetValue(TKey key, out TValue value)
    {
        int count = _count;
        if (count != 0)
        {
            uint hashCode = (uint)key.GetHashCode();
            int i = _buckets[hashCode & _bucketMask] + _bucketBias;
            if ((uint)i < (uint)count)
            {
                Entry[] entries = _entries;
                // Next descends within the build or goes negative; guarding on entries.Length elides the bounds
                // check. The hop cap (a valid chain has at most count hops) bounds the walk even if a caller
                // violates the lease and reads during a rebuild - a torn walk misses instead of spinning.
                for (int hops = count; hops > 0 && (uint)i < (uint)entries.Length; hops--)
                {
                    ref Entry entry = ref entries[i];
                    if (entry.HashCode == hashCode && entry.Key.Equals(key))
                    {
                        value = entry.Value;
                        return true;
                    }
                    i = entry.Next;
                }
            }
        }

        value = default!;
        return false;
    }

    public void BuildFromUnsorted<TComparer>(IReadOnlyCollection<KeyValuePair<TKey, TValue>> source, TComparer keyComparer)
        where TComparer : IComparer<TKey>
    {
        int count = source.Count;
        _count = 0; // a build that throws must leave the dictionary empty, not mixing entries of two builds
        EnsureEntryCapacity(count);
        int i = 0;
        foreach (KeyValuePair<TKey, TValue> kv in source)
        {
            // A concurrently mutated source can yield more items than Count; never write past the dirty watermark.
            if (i == count) break;
            _entries[i++] = new Entry { HashCode = (uint)kv.Key.GetHashCode(), Key = kv.Key, Value = kv.Value };
        }

        count = i; // it can also yield fewer; only what was written is the build
        _entries.AsSpan(0, count).Sort(new EntryKeyComparer<TComparer>(keyComparer));
        _count = count;
        BuildBuckets();
    }

    /// <summary>
    /// Merges already-sorted inputs (ascending priority; the highest-index source wins on equal keys). When
    /// <paramref name="keep"/> is supplied, entries it rejects are dropped and keys with no survivor are omitted.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TComparer"/> is a type parameter rather than an <see cref="IComparer{T}"/> parameter so
    /// that struct comparers are monomorphized: their Compare calls are devirtualized and inlined at every JIT tier.
    /// </remarks>
    public void BuildFromMerge<TComparer>(
        ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources,
        TComparer keyComparer,
        Func<int, TKey, bool>? keep = null)
        where TComparer : IComparer<TKey>
    {
        _count = 0; // a build that throws must leave the dictionary empty, not mixing entries of two builds
        if (sources.Length == 0) return;

        int total = 0;
        foreach (SortedMergeDictionary<TKey, TValue> source in sources) total += source._count;
        EnsureEntryCapacity(total);

        LoserTree<TComparer> tree = new(sources, keyComparer);
        Entry[] entries = _entries;
        int count = 0;
        // count < total also guards the dirty watermark against sources mutated in violation of their lease.
        while (count < total && tree.TryNext(keep, out Entry chosen))
        {
            entries[count++] = chosen;
        }

        _count = count;
        BuildBuckets();
    }

    public void NoResizeClear()
    {
        // Stale bucket stamps are invalidated by the next build's salt advance.
        if (_entriesDirty > 0) Array.Clear(_entries, 0, _entriesDirty);
        _count = 0;
        _entriesDirty = 0;
    }

    public void Dispose()
    {
        if (_entries.Length > 0)
        {
            if (_entriesDirty > 0) Array.Clear(_entries, 0, _entriesDirty);
            ArrayPool<Entry>.Shared.Return(_entries);
            _entries = [];
        }
        if (_buckets.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_buckets);
            _buckets = [];
        }
        _count = 0;
        _entriesDirty = 0;
        _bucketSalt = 0;
        _bucketsCleared = 0;
    }

    private void EnsureEntryCapacity(int count)
    {
        Entry[] entries = _entries;
        if (entries.Length < count)
        {
            _entries = ArrayPool<Entry>.Shared.Rent(count);
            if (entries.Length > 0)
            {
                if (_entriesDirty > 0) Array.Clear(entries, 0, _entriesDirty);
                ArrayPool<Entry>.Shared.Return(entries);
            }
            _entriesDirty = count;
        }
        else if (count > _entriesDirty)
        {
            // Raised before any write so an aborted build is still fully cleared on reset/return.
            _entriesDirty = count;
        }
    }

    private void BuildBuckets()
    {
        int count = _count;
        if (count == 0) return; // reads are gated on _count

        int size = BucketSize(count);
        int[] buckets = _buckets;
        int salt;
        if (buckets.Length < size)
        {
            int[] old = buckets;
            _buckets = buckets = ArrayPool<int>.Shared.Rent(size);
            // Foreign garbage could alias stamps; zero decodes as empty for every salt.
            Array.Clear(buckets, 0, size);
            _bucketsCleared = size;
            salt = 0;
            if (old.Length > 0) ArrayPool<int>.Shared.Return(old);
        }
        else
        {
            if (size > _bucketsCleared)
            {
                // Grow the zeroed prefix: beyond it is pre-rent garbage the salt cannot invalidate.
                Array.Clear(buckets, _bucketsCleared, size - _bucketsCleared);
                _bucketsCleared = size;
            }
            salt = _bucketSalt;
            if (salt > int.MaxValue - count)
            {
                // Restart the stamp sequence before it overflows (once per ~2 billion stamped entries).
                Array.Clear(buckets, 0, _bucketsCleared);
                salt = 0;
            }
        }

        _bucketSalt = salt + count;
        int bias = -1 - salt;
        _bucketBias = bias;
        _bucketMask = (uint)(size - 1);

        // A slot stores (i + 1 + salt); slots from earlier builds decode negative and read as empty.
        Span<int> bucketSpan = buckets.AsSpan(0, size);
        Span<Entry> entries = _entries.AsSpan(0, count);
        for (int i = 0; i < entries.Length; i++)
        {
            ref Entry entry = ref entries[i];
            ref int bucket = ref bucketSpan[(int)entry.HashCode & (bucketSpan.Length - 1)];
            entry.Next = bucket + bias;
            bucket = i - bias;
        }
    }

    public static SortedMergeDictionary<TKey, TValue> FromUnsorted<TComparer>(
        IReadOnlyCollection<KeyValuePair<TKey, TValue>> source, TComparer keyComparer)
        where TComparer : IComparer<TKey>
    {
        SortedMergeDictionary<TKey, TValue> dictionary = new();
        dictionary.BuildFromUnsorted(source, keyComparer);
        return dictionary;
    }

    public static SortedMergeDictionary<TKey, TValue> Merge<TComparer>(
        ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources,
        TComparer keyComparer,
        Func<int, TKey, bool>? keep = null)
        where TComparer : IComparer<TKey>
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

    private readonly struct EntryKeyComparer<TComparer>(TComparer keyComparer) : IComparer<Entry>
        where TComparer : IComparer<TKey>
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
    private ref struct LoserTree<TComparer> where TComparer : IComparer<TKey>
    {
        private readonly ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> _sources;
        private readonly TComparer _keyComparer;
        private readonly int _k;
        private readonly int[] _tree;
        private readonly int[] _position;

        public LoserTree(ReadOnlySpan<SortedMergeDictionary<TKey, TValue>> sources, TComparer keyComparer)
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
