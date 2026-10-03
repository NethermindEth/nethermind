// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash map with the <see cref="Dictionary{TKey,TValue}"/> surface the state path uses.
/// </summary>
/// <remarks>
/// The guest build is <see cref="OptimizedHashTable{TKey,TValue}"/>: word-sized fields and links, a power-of-two bucket
/// array and direct hash and equality calls for value-type keys. Enumeration order matches <see cref="Dictionary{TKey,TValue}"/>.
/// </remarks>
public sealed class OptimizedDictionary<TKey, TValue>(int capacity, IEqualityComparer<TKey>? comparer) : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue> where TKey : notnull
{
    private OptimizedHashTable<TKey, TValue> _table = new(capacity, comparer);
    private KeyCollection? _keys;
    private ValueCollection? _values;

    public OptimizedDictionary() : this(0, null) { }

    public OptimizedDictionary(int capacity) : this(capacity, null) { }

    public OptimizedDictionary(IEqualityComparer<TKey>? comparer) : this(0, comparer) { }

    public int Count => _table.Count;

    public int Capacity => _table.Capacity;

    public IEqualityComparer<TKey> Comparer => _table.Comparer;

    public bool IsReadOnly => false;

    public KeyCollection Keys => _keys ??= new(this);

    public ValueCollection Values => _values ??= new(this);

    ICollection<TKey> IDictionary<TKey, TValue>.Keys => Keys;
    ICollection<TValue> IDictionary<TKey, TValue>.Values => Values;
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    public TValue this[TKey key]
    {
        get
        {
            ref TValue value = ref GetValueRefOrNullRef(key);
            if (Unsafe.IsNullRef(ref value)) ThrowKeyNotFound(key);
            return value;
        }
        set
        {
            ThrowIfNull(key);
            _table.FindOrAdd(key, out _).Value = value;
        }
    }

    /// <summary>Returns a reference to the value of <paramref name="key"/>, adding a default one if it is absent.</summary>
    /// <remarks>The reference is valid until the next addition or removal.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue? GetValueRefOrAddDefault(scoped in TKey key, out bool exists)
    {
        ThrowIfNull(key);
        return ref _table.FindOrAdd(in key, out exists).Value!;
    }

    /// <summary>Returns a reference to the value of <paramref name="key"/>, or a null reference if it is absent.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue GetValueRefOrNullRef(scoped in TKey key)
    {
        ThrowIfNull(key);
        ref OptimizedHashTable<TKey, TValue>.Entry entry = ref _table.FindEntry(in key);
        return ref Unsafe.IsNullRef(ref entry) ? ref Unsafe.NullRef<TValue>() : ref entry.Value;
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ref TValue found = ref GetValueRefOrNullRef(key);
        if (Unsafe.IsNullRef(ref found))
        {
            value = default;
            return false;
        }

        value = found;
        return true;
    }

    public bool ContainsKey(TKey key) => !Unsafe.IsNullRef(ref GetValueRefOrNullRef(key));

    public void Add(TKey key, TValue value)
    {
        ThrowIfNull(key);
        ref TValue slot = ref _table.FindOrAdd(key, out bool exists).Value;
        if (exists) ThrowDuplicateKey(key);
        slot = value;
    }

    public bool TryAdd(TKey key, TValue value)
    {
        ThrowIfNull(key);
        ref TValue slot = ref _table.FindOrAdd(key, out bool exists).Value;
        if (exists) return false;
        slot = value;
        return true;
    }

    public bool Remove(TKey key) => Remove(key, out _);

    public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ThrowIfNull(key);
        return _table.Remove(in key, out value);
    }

    public void Clear() => _table.Clear();

    public int EnsureCapacity(int capacity) => _table.EnsureCapacity(capacity);

    public void TrimExcess() => _table.TrimExcess(Count);

    public void TrimExcess(int capacity) => _table.TrimExcess(capacity);

    /// <summary>Clears the map, shrinking it to <paramref name="trimToCapacity"/> when it grew past <paramref name="trimAboveCapacity"/>.</summary>
    public void ClearAndTrim(int trimAboveCapacity = CollectionExtensions.DefaultTrimAboveCapacity, int trimToCapacity = CollectionExtensions.DefaultTrimToCapacity)
    {
        Clear();
        if (Capacity > trimAboveCapacity) TrimExcess(trimToCapacity);
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
    {
        ref TValue value = ref GetValueRefOrNullRef(item.Key);
        return !Unsafe.IsNullRef(ref value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item) =>
        ((ICollection<KeyValuePair<TKey, TValue>>)this).Contains(item) && Remove(item.Key);

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        foreach (KeyValuePair<TKey, TValue> pair in this) array[arrayIndex++] = pair;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowIfNull(scoped in TKey key)
    {
        if (!typeof(TKey).IsValueType && key is null) ThrowKeyNull();
    }

    [DoesNotReturn]
    private static void ThrowKeyNull() => throw new ArgumentNullException("key");

    [DoesNotReturn]
    private static void ThrowKeyNotFound(TKey key) => throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");

    [DoesNotReturn]
    private static void ThrowDuplicateKey(TKey key) => throw new ArgumentException($"An item with the same key has already been added. Key: {key}");

    /// <summary>Walks the live entries in storage order.</summary>
    public struct Enumerator(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly OptimizedHashTable<TKey, TValue>.Entry[] _entries = dictionary._table.Entries;
        private readonly nint _used = dictionary._table.Used;
        private nint _index = -1;

        public bool MoveNext()
        {
            while (++_index < _used)
            {
                if (OptimizedHashTable<TKey, TValue>.IsLive(in CurrentEntry)) return true;
            }

            return false;
        }

        public readonly KeyValuePair<TKey, TValue> Current => new(CurrentEntry.Key, CurrentEntry.Value);

        internal readonly ref OptimizedHashTable<TKey, TValue>.Entry CurrentEntry =>
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), _index);

        readonly object IEnumerator.Current => Current;

        public void Reset() => _index = -1;

        public readonly void Dispose() { }
    }

    public sealed class KeyCollection(OptimizedDictionary<TKey, TValue> dictionary) : ICollection<TKey>, IReadOnlyCollection<TKey>
    {
        public int Count => dictionary.Count;
        public bool IsReadOnly => true;
        public Enumerator GetEnumerator() => new(dictionary);
        IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Contains(TKey item) => dictionary.ContainsKey(item);

        public void CopyTo(TKey[] array, int arrayIndex)
        {
            foreach (TKey key in this) array[arrayIndex++] = key;
        }

        void ICollection<TKey>.Add(TKey item) => throw new NotSupportedException();
        void ICollection<TKey>.Clear() => throw new NotSupportedException();
        bool ICollection<TKey>.Remove(TKey item) => throw new NotSupportedException();

        public struct Enumerator(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerator<TKey>
        {
            private OptimizedDictionary<TKey, TValue>.Enumerator _inner = new(dictionary);
            public bool MoveNext() => _inner.MoveNext();
            public readonly TKey Current => _inner.CurrentEntry.Key;
            readonly object IEnumerator.Current => Current;
            public void Reset() => _inner.Reset();
            public readonly void Dispose() { }
        }
    }

    public sealed class ValueCollection(OptimizedDictionary<TKey, TValue> dictionary) : ICollection<TValue>, IReadOnlyCollection<TValue>
    {
        public int Count => dictionary.Count;
        public bool IsReadOnly => true;
        public Enumerator GetEnumerator() => new(dictionary);
        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool Contains(TValue item)
        {
            foreach (TValue value in this)
            {
                if (EqualityComparer<TValue>.Default.Equals(value, item)) return true;
            }

            return false;
        }

        public void CopyTo(TValue[] array, int arrayIndex)
        {
            foreach (TValue value in this) array[arrayIndex++] = value;
        }

        void ICollection<TValue>.Add(TValue item) => throw new NotSupportedException();
        void ICollection<TValue>.Clear() => throw new NotSupportedException();
        bool ICollection<TValue>.Remove(TValue item) => throw new NotSupportedException();

        public struct Enumerator(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerator<TValue>
        {
            private OptimizedDictionary<TKey, TValue>.Enumerator _inner = new(dictionary);
            public bool MoveNext() => _inner.MoveNext();
            public readonly TValue Current => _inner.CurrentEntry.Value;
            readonly object IEnumerator.Current => Current!;
            public void Reset() => _inner.Reset();
            public readonly void Dispose() { }
        }
    }
}
