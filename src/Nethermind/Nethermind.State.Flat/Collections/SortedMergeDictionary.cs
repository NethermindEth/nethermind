// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// A cuckoo index slot: the key's full 32-bit hash plus a 1-based index into the sorted entry array
/// (0 == empty, so <see cref="Array.Clear(Array)"/> resets a table). Non-generic so every
/// <see cref="SortedMergeDictionary{TKey,TValue}"/> instantiation shares one <see cref="ArrayPool{T}"/>.
/// </summary>
internal struct CuckooSlot
{
    public uint HashCode;
    public int EntryIndex;
}

/// <summary>
/// Build-once, read-only dictionary for k-way merging compacted snapshot content: entries kept sorted by key
/// with a bucketized cuckoo hash index (two candidate buckets of two slots each; hashes live in the index, so
/// entries hold only key and value), so lookups are O(1) and enumeration is in key order. Backing arrays are
/// pooled and reused across builds via <see cref="NoResizeClear"/>.
/// </summary>
/// <remarks>
/// Items still homeless after capped table growth — five or more keys sharing one full 32-bit hash can never fit
/// their four fixed slots — spill to an overflow list scanned only when non-empty, so a build never fails.
/// </remarks>
internal sealed class SortedMergeDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IDisposable
    where TKey : IEquatable<TKey>
{
    private struct Entry
    {
        public TKey Key;
        public TValue Value;
    }

    private Entry[] _entries = [];
    private CuckooSlot[] _slots = []; // bucket b occupies slots [2b, 2b+1]
    private List<CuckooSlot>? _overflow;
    private int _count;
    private int _bucketCount;
    private uint _bucketMask;

    public int Count => _count;

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_count != 0)
        {
            uint hashCode = (uint)key.GetHashCode();
            // Distinct keys can share a full 32-bit hash, so a hash match with a key mismatch must keep probing.
            if (TryMatchBucket(hashCode & _bucketMask, hashCode, key, out value)) return true;
            if (TryMatchBucket(SecondBucket(hashCode), hashCode, key, out value)) return true;
            if (_overflow is { Count: > 0 } overflow)
            {
                for (int i = 0; i < overflow.Count; i++)
                {
                    CuckooSlot slot = overflow[i];
                    if (TryMatchSlot(in slot, hashCode, key, out value)) return true;
                }
            }
        }

        value = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryMatchBucket(uint bucket, uint hashCode, TKey key, out TValue value)
    {
        int first = (int)bucket * SlotsPerBucket;
        return TryMatchSlot(in _slots[first], hashCode, key, out value)
            || TryMatchSlot(in _slots[first + 1], hashCode, key, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryMatchSlot(in CuckooSlot slot, uint hashCode, TKey key, out TValue value)
    {
        // Empty slots have HashCode == 0, which a genuine zero hash would match — check occupancy first.
        if (slot.EntryIndex != 0 && slot.HashCode == hashCode)
        {
            ref Entry entry = ref _entries[slot.EntryIndex - 1];
            if (entry.Key.Equals(key))
            {
                value = entry.Value;
                return true;
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
            _entries[i++] = new Entry { Key = kv.Key, Value = kv.Value };
        }

        Array.Sort(_entries, 0, count, new EntryKeyComparer(keyComparer));
        _count = count;
        BuildIndex();
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
            BuildIndex();
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
        BuildIndex();
    }

    public void NoResizeClear()
    {
        if (_count > 0) Array.Clear(_entries, 0, _count);
        if (_bucketCount > 0) Array.Clear(_slots, 0, _bucketCount * SlotsPerBucket);
        _overflow?.Clear();
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
        if (_slots.Length > 0)
        {
            ArrayPool<CuckooSlot>.Shared.Return(_slots);
            _slots = [];
        }
        _overflow = null;
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

    private void BuildIndex()
    {
        int size = BucketCount(_count);
        int maxSize = size << 2; // two doublings of headroom; beyond that only identical-hash pathology remains
        while (true)
        {
            if (_slots.Length < size * SlotsPerBucket)
            {
                CuckooSlot[] old = _slots;
                _slots = ArrayPool<CuckooSlot>.Shared.Rent(size * SlotsPerBucket);
                if (old.Length > 0) ArrayPool<CuckooSlot>.Shared.Return(old);
            }

            _bucketCount = size;
            _bucketMask = (uint)(size - 1);
            Array.Clear(_slots, 0, size * SlotsPerBucket);
            _overflow?.Clear();

            // The final attempt (at the growth cap) spills homeless items to _overflow instead of failing.
            if (TryFillIndex(spill: size >= maxSize)) return;
            size <<= 1;
        }
    }

    private bool TryFillIndex(bool spill)
    {
        for (int i = 0; i < _count; i++)
        {
            // HashedKey<T> caches its hash, so this is a field read for the real key types.
            if (!TryInsert((uint)_entries[i].Key.GetHashCode(), i + 1, out CuckooSlot homeless))
            {
                if (!spill) return false;
                (_overflow ??= []).Add(homeless);
            }
        }
        return true;
    }

    private bool TryInsert(uint hashCode, int entryIndex, out CuckooSlot homeless)
    {
        CuckooSlot item = new() { HashCode = hashCode, EntryIndex = entryIndex };
        homeless = default;
        uint bucket = hashCode & _bucketMask;
        if (TryPlace(bucket, in item) || TryPlace(SecondBucket(hashCode), in item)) return true;

        for (int kicks = 0; kicks < MaxKicks; kicks++)
        {
            ref CuckooSlot victim = ref _slots[(int)bucket * SlotsPerBucket + (kicks & 1)]; // alternate the victim slot
            (item, victim) = (victim, item);
            bucket = AltBucket(item.HashCode, bucket); // the stored hash locates the evictee's other bucket — entries untouched
            if (TryPlace(bucket, in item)) return true;
        }

        homeless = item;
        return false;
    }

    private bool TryPlace(uint bucket, in CuckooSlot item)
    {
        int first = (int)bucket * SlotsPerBucket;
        ref CuckooSlot slot0 = ref _slots[first];
        if (slot0.EntryIndex == 0)
        {
            slot0 = item;
            return true;
        }
        ref CuckooSlot slot1 = ref _slots[first + 1];
        if (slot1.EntryIndex == 0)
        {
            slot1 = item;
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint SecondBucket(uint hash) => (uint)((hash * 0x9E3779B97F4A7C15UL) >> 32) & _bucketMask;

    private uint AltBucket(uint hash, uint bucket)
    {
        uint first = hash & _bucketMask;
        return bucket == first ? SecondBucket(hash) : first;
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

    private const int SlotsPerBucket = 2;
    // At load <= 0.75 a displacement chain beyond 32 almost certainly means an irresolvable cycle;
    // a false positive only costs one doubling.
    private const int MaxKicks = 32;

    // Load factor <= 0.75 over 2-slot buckets (the 2-slot cuckoo threshold is ~0.89).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketCount(int count) =>
        count == 0 ? 1 : (int)BitOperations.RoundUpToPowerOf2((uint)(count * 2 / 3) + 1);

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
