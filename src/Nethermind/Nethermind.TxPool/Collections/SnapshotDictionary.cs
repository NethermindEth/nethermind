// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Nethermind.TxPool.Collections;

/// <summary>A read-only dictionary over an owned array of unique snapshot entries.</summary>
/// <remarks>
/// The caller transfers ownership of the array. A compact open-addressed index leaves entries contiguous for scans.
/// The index is built on the first keyed lookup so value-only scans need no index allocation.
/// Its low bits hold one-based entry indices; unused high bits hold hash fingerprints to avoid unnecessary key reads.
/// </remarks>
internal sealed class SnapshotDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue> where TKey : notnull
{
    private const uint HashMultiplier = 2654435769u;
    private readonly KeyValuePair<TKey, TValue>[] _entries;
    private int[]? _index;
    private readonly int _shift;
    private readonly KeyCollection _keys;
    private readonly ValueCollection _values;
    private readonly int _count;

    public int Count => _count;
    public bool IsReadOnly => true;
    public ICollection<TKey> Keys => _keys;
    public ICollection<TValue> Values => _values;
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _values;

    /// <remarks>The first <paramref name="count"/> entries must have unique, non-null keys.</remarks>
    public SnapshotDictionary(KeyValuePair<TKey, TValue>[] entries, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, entries.Length);
        _entries = entries;
        _count = count;
        // At most half full: indices fit in the slot mask and every probe terminates at an empty slot.
        int size = Math.Max(2, checked((int)BitOperations.RoundUpToPowerOf2((ulong)count * 2)));
        _shift = BitOperations.LeadingZeroCount((uint)(size - 1));
        _keys = new(this);
        _values = new(this);
    }

    private int[] CreateIndex()
    {
        int[] index = new int[1 << (32 - _shift)];
        int mask = index.Length - 1;
        for (int i = 0; i < _count; i++)
        {
            uint hash = unchecked((uint)EqualityComparer<TKey>.Default.GetHashCode(_entries[i].Key));
            int slot = GetSlot(hash);
            while (index[slot] != 0) slot = (slot + 1) & mask;
            index[slot] = unchecked((int)((hash & ~(uint)mask) | (uint)(i + 1)));
        }

        return Interlocked.CompareExchange(ref _index, index, null) ?? index;
    }

    // Multiplication before taking high bits avoids clustering keys with similar low hash bits.
    private int GetSlot(uint hash) => (int)(unchecked(hash * HashMultiplier) >> _shift);

    public TValue this[TKey key]
    {
        get => TryGetValue(key, out TValue? value) ? value : throw new KeyNotFoundException();
        set => throw new NotSupportedException();
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        int[] index = Volatile.Read(ref _index) ?? CreateIndex();
        uint hash = unchecked((uint)EqualityComparer<TKey>.Default.GetHashCode(key));
        int mask = index.Length - 1, slot = GetSlot(hash);
        while (index[slot] != 0)
        {
            if ((((uint)index[slot] ^ hash) & ~(uint)mask) == 0)
            {
                ref readonly KeyValuePair<TKey, TValue> entry = ref _entries[(index[slot] & mask) - 1];
                if (EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                {
                    value = entry.Value;
                    return true;
                }
            }

            slot = (slot + 1) & mask;
        }

        value = default!;
        return false;
    }

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);
    public bool Contains(KeyValuePair<TKey, TValue> item) => TryGetValue(item.Key, out TValue? value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => Array.Copy(_entries, 0, array, arrayIndex, _count);
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return _entries[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(TKey key, TValue value) => throw new NotSupportedException();
    public void Add(KeyValuePair<TKey, TValue> item) => throw new NotSupportedException();
    public bool Remove(TKey key) => throw new NotSupportedException();
    public bool Remove(KeyValuePair<TKey, TValue> item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();

    private abstract class Collection<T>(SnapshotDictionary<TKey, TValue> map) : ICollection<T>
    {
        protected SnapshotDictionary<TKey, TValue> Map { get; } = map;
        public int Count => Map.Count;
        public bool IsReadOnly => true;
        public abstract IEnumerator<T> GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public abstract bool Contains(T item);
        public void CopyTo(T[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
            if (arrayIndex > array.Length || Count > array.Length - arrayIndex) throw new ArgumentException();
            foreach (T value in this) array[arrayIndex++] = value;
        }

        public void Add(T item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Remove(T item) => throw new NotSupportedException();
    }

    private sealed class KeyCollection(SnapshotDictionary<TKey, TValue> map) : Collection<TKey>(map)
    {
        public override bool Contains(TKey item) => Map.ContainsKey(item);
        public override IEnumerator<TKey> GetEnumerator()
        {
            for (int i = 0; i < Map._count; i++) yield return Map._entries[i].Key;
        }
    }

    private sealed class ValueCollection(SnapshotDictionary<TKey, TValue> map) : Collection<TValue>(map)
    {
        public override bool Contains(TValue item)
        {
            for (int i = 0; i < Map._count; i++)
                if (EqualityComparer<TValue>.Default.Equals(Map._entries[i].Value, item)) return true;
            return false;
        }

        public override IEnumerator<TValue> GetEnumerator() => new ValueEnumerator(Map);

        private sealed class ValueEnumerator(SnapshotDictionary<TKey, TValue> map) : IEnumerator<TValue>
        {
            private int _position = -1;
            public TValue Current => (uint)_position < (uint)map._count ? map._entries[_position].Value : default!;
            object? IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (_position < map._count - 1)
                {
                    _position++;
                    return true;
                }
                _position = map._count;
                return false;
            }
            public void Dispose() => _position = map._count;
            public void Reset() => throw new NotSupportedException();
        }
    }
}
