// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash map with the <see cref="Dictionary{TKey,TValue}"/> members its callers use, tuned for the zkVM guest.
/// </summary>
/// <remarks>
/// Keeps <see cref="Dictionary{TKey,TValue}"/>'s layout and algorithm, so enumeration order, removal during enumeration
/// and the free list behave the same, but every field, bucket and entry link a probe reads is a 64-bit word: the
/// guest's emulator charges about six times as much for a 32-bit load or store as for an aligned 64-bit one. Buckets are
/// a power of two indexed by a multiplicative hash rather than a prime with a fast-modulo reduction, and the tables are
/// indexed without bounds checks. As in <see cref="Dictionary{TKey,TValue}"/>, each probe loop spells out the key
/// comparison: a helper taking the keys by value would copy a struct key per probe.
/// <para>
/// Removed and cleared entries keep their contents: nothing reads an entry past the count or on the free list, and the
/// guest has no garbage collector for them to hold objects from. Single-threaded, and an enumerator does not detect a
/// modification made while it runs.
/// </para>
/// </remarks>
public sealed class OptimizedDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
{
    // Mirrors Dictionary's encoding: a free entry's Next is StartOfFreeList minus the next free index, so live entries
    // are the ones with Next >= -1.
    private const long StartOfFreeList = -3;

    private struct Entry
    {
        public ulong HashCode;
        public long Next;
        public TKey Key;
        public TValue Value;
    }

    private readonly IEqualityComparer<TKey>? _comparer;
    // One-based entry index per bucket; zero marks an empty bucket.
    private long[]? _buckets;
    private Entry[] _entries = [];
    private long _size;
    private long _shift;
    private long _count;
    private long _freeList = -1;
    private long _freeCount;

    public OptimizedDictionary() : this(0, null) { }

    public OptimizedDictionary(int capacity) : this(capacity, null) { }

    public OptimizedDictionary(IEqualityComparer<TKey>? comparer) : this(0, comparer) { }

    public OptimizedDictionary(int capacity, IEqualityComparer<TKey>? comparer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > 0) Initialize(capacity);
        _comparer = OptimizedHashing.ResolveComparer(comparer);
    }

    public int Count => (int)(_count - _freeCount);

    /// <summary>The number of entries the map holds before it grows.</summary>
    public int Capacity => (int)_size;

    public TValue this[TKey key]
    {
        get
        {
            ref TValue value = ref FindValue(key);
            if (Unsafe.IsNullRef(ref value)) ThrowKeyNotFound(key);
            return value;
        }
        set => GetValueRefOrAddDefault(key, out _) = value;
    }

    public KeyCollection Keys => new(this);

    public ValueCollection Values => new(this);

    public bool ContainsKey(TKey key) => !Unsafe.IsNullRef(ref FindValue(key));

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ref TValue found = ref FindValue(key);
        if (Unsafe.IsNullRef(ref found))
        {
            value = default;
            return false;
        }

        value = found;
        return true;
    }

    public void Add(TKey key, TValue value)
    {
        if (!TryAdd(key, value)) ThrowDuplicateKey(key);
    }

    public bool TryAdd(TKey key, TValue value)
    {
        ref TValue? slot = ref GetValueRefOrAddDefault(key, out bool exists);
        if (exists) return false;
        slot = value;
        return true;
    }

    /// <inheritdoc cref="System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef{TKey,TValue}(Dictionary{TKey,TValue},TKey)"/>
    public ref TValue GetValueRefOrNullRef(TKey key) => ref FindValue(key);

    /// <inheritdoc cref="System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault{TKey,TValue}(Dictionary{TKey,TValue},TKey,out bool)"/>
    public ref TValue? GetValueRefOrAddDefault(TKey key, out bool exists)
    {
        if (_buckets is null) Initialize(0);

        IEqualityComparer<TKey>? comparer = _comparer;
        ulong hashCode = GetHashCode(comparer, key);
        ref long bucket = ref OptimizedHashing.At(_buckets!, OptimizedHashing.BucketIndex(hashCode, _shift));
        Entry[] entries = _entries;
        long i = bucket - 1;
        while (i >= 0)
        {
            ref Entry entry = ref OptimizedHashing.At(entries, i);
            if (entry.HashCode == hashCode
                && (typeof(TKey).IsValueType && comparer is null
                    ? EqualityComparer<TKey>.Default.Equals(entry.Key, key)
                    : comparer!.Equals(entry.Key, key)))
            {
                exists = true;
                return ref entry.Value!;
            }

            i = entry.Next;
        }

        long index;
        if (_freeCount > 0)
        {
            index = _freeList;
            _freeList = StartOfFreeList - OptimizedHashing.At(entries, index).Next;
            _freeCount--;
        }
        else
        {
            if (_count == _size)
            {
                Resize(OptimizedHashing.GrowSize((int)_size));
                bucket = ref OptimizedHashing.At(_buckets!, OptimizedHashing.BucketIndex(hashCode, _shift));
                entries = _entries;
            }

            index = _count;
            _count++;
        }

        ref Entry added = ref OptimizedHashing.At(entries, index);
        added.HashCode = hashCode;
        added.Next = bucket - 1;
        added.Key = key;
        added.Value = default!;
        bucket = index + 1;
        exists = false;
        return ref added.Value!;
    }

    public bool Remove(TKey key) => Remove(key, out _);

    public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_buckets is not null)
        {
            IEqualityComparer<TKey>? comparer = _comparer;
            ulong hashCode = GetHashCode(comparer, key);
            ref long bucket = ref OptimizedHashing.At(_buckets, OptimizedHashing.BucketIndex(hashCode, _shift));
            Entry[] entries = _entries;
            long last = -1;
            long i = bucket - 1;
            while (i >= 0)
            {
                ref Entry entry = ref OptimizedHashing.At(entries, i);
                if (entry.HashCode == hashCode
                    && (typeof(TKey).IsValueType && comparer is null
                        ? EqualityComparer<TKey>.Default.Equals(entry.Key, key)
                        : comparer!.Equals(entry.Key, key)))
                {
                    if (last < 0) bucket = entry.Next + 1;
                    else OptimizedHashing.At(entries, last).Next = entry.Next;

                    value = entry.Value;
                    entry.Next = StartOfFreeList - _freeList;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }

                last = i;
                i = entry.Next;
            }
        }

        value = default;
        return false;
    }

    public void Clear()
    {
        if (_count == 0) return;
        long[] buckets = _buckets!;
        // Few entries in a large table: unlinking each entry's bucket writes less than zeroing the table.
        if (_count < _size >> 3)
        {
            Entry[] entries = _entries;
            for (long i = 0; i < _count; i++)
            {
                OptimizedHashing.At(buckets, OptimizedHashing.BucketIndex(OptimizedHashing.At(entries, i).HashCode, _shift)) = 0;
            }
        }
        else
        {
            Array.Clear(buckets);
        }

        _count = 0;
        _freeList = -1;
        _freeCount = 0;
    }

    /// <summary>Clears the map and, when its capacity exceeds <paramref name="trimAboveCapacity"/>, shrinks it to fit <paramref name="trimToCapacity"/>.</summary>
    /// <remarks>Mirrors <c>ClearAndTrim</c> of <see cref="DictionaryExtensions"/> for <see cref="Dictionary{TKey,TValue}"/>.</remarks>
    public void ClearAndTrim(int trimAboveCapacity = CollectionExtensions.DefaultTrimAboveCapacity, int trimToCapacity = CollectionExtensions.DefaultTrimToCapacity)
    {
        Clear();
        if (Capacity > trimAboveCapacity && OptimizedHashing.SizeFor(trimToCapacity) < Capacity)
        {
            Initialize(trimToCapacity);
        }
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private ref TValue FindValue(TKey key)
    {
        if (_buckets is not null)
        {
            IEqualityComparer<TKey>? comparer = _comparer;
            ulong hashCode = GetHashCode(comparer, key);
            Entry[] entries = _entries;
            long i = OptimizedHashing.At(_buckets, OptimizedHashing.BucketIndex(hashCode, _shift)) - 1;
            while (i >= 0)
            {
                ref Entry entry = ref OptimizedHashing.At(entries, i);
                if (entry.HashCode == hashCode
                    && (typeof(TKey).IsValueType && comparer is null
                        ? EqualityComparer<TKey>.Default.Equals(entry.Key, key)
                        : comparer!.Equals(entry.Key, key)))
                {
                    return ref entry.Value;
                }
                i = entry.Next;
            }
        }

        return ref Unsafe.NullRef<TValue>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetHashCode(IEqualityComparer<TKey>? comparer, TKey key) =>
        (uint)(typeof(TKey).IsValueType && comparer is null
            ? EqualityComparer<TKey>.Default.GetHashCode(key)
            : comparer!.GetHashCode(key));


    private void Initialize(int capacity)
    {
        int size = OptimizedHashing.SizeFor(capacity);
        _buckets = new long[size];
        _entries = new Entry[size];
        _size = size;
        _shift = OptimizedHashing.ShiftFor(size);
        _count = 0;
        _freeList = -1;
        _freeCount = 0;
    }

    private void Resize(int newSize)
    {
        Entry[] entries = new Entry[newSize];
        Array.Copy(_entries, entries, (int)_count);
        long[] buckets = new long[newSize];
        long shift = OptimizedHashing.ShiftFor(newSize);
        for (long i = 0; i < _count; i++)
        {
            ref Entry entry = ref entries[i];
            if (entry.Next >= -1)
            {
                ref long bucket = ref buckets[OptimizedHashing.BucketIndex(entry.HashCode, shift)];
                entry.Next = bucket - 1;
                bucket = i + 1;
            }
        }

        _buckets = buckets;
        _entries = entries;
        _size = newSize;
        _shift = shift;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowKeyNotFound(TKey key) => throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowDuplicateKey(TKey key) => throw new ArgumentException($"An item with the same key has already been added. Key: {key}");

    /// <summary>Advances <paramref name="index"/> past the next live entry of <paramref name="dictionary"/>.</summary>
    /// <returns>That entry, or a null reference once the entries are exhausted.</returns>
    private static ref Entry NextEntry(OptimizedDictionary<TKey, TValue> dictionary, ref long index)
    {
        Entry[] entries = dictionary._entries;
        long count = dictionary._count;
        while (index < count)
        {
            ref Entry entry = ref OptimizedHashing.At(entries, index++);
            if (entry.Next >= -1) return ref entry;
        }

        return ref Unsafe.NullRef<Entry>();
    }

    /// <summary>Enumerates the entries in <see cref="Dictionary{TKey,TValue}"/>'s order.</summary>
    public struct Enumerator(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private long _index;

        public KeyValuePair<TKey, TValue> Current { readonly get; private set; }

        public bool MoveNext()
        {
            ref Entry entry = ref NextEntry(dictionary, ref _index);
            if (Unsafe.IsNullRef(ref entry))
            {
                Current = default;
                return false;
            }

            Current = new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
            return true;
        }

        readonly object IEnumerator.Current => Current;

        void IEnumerator.Reset()
        {
            _index = 0;
            Current = default;
        }

        public readonly void Dispose() { }
    }

    /// <summary>The keys of an <see cref="OptimizedDictionary{TKey,TValue}"/>, in its enumeration order.</summary>
    public readonly struct KeyCollection(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerable<TKey>
    {
        public int Count => dictionary.Count;

        public KeyEnumerator GetEnumerator() => new(dictionary);

        IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>The values of an <see cref="OptimizedDictionary{TKey,TValue}"/>, in its enumeration order.</summary>
    public readonly struct ValueCollection(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerable<TValue>
    {
        public int Count => dictionary.Count;

        public ValueEnumerator GetEnumerator() => new(dictionary);

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public struct KeyEnumerator(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerator<TKey>
    {
        private long _index;

        public TKey Current { readonly get; private set; } = default!;

        public bool MoveNext()
        {
            ref Entry entry = ref NextEntry(dictionary, ref _index);
            if (Unsafe.IsNullRef(ref entry))
            {
                Current = default!;
                return false;
            }

            Current = entry.Key;
            return true;
        }

        readonly object IEnumerator.Current => Current;

        void IEnumerator.Reset()
        {
            _index = 0;
            Current = default!;
        }

        public readonly void Dispose() { }
    }

    public struct ValueEnumerator(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerator<TValue>
    {
        private long _index;

        public TValue Current { readonly get; private set; } = default!;

        public bool MoveNext()
        {
            ref Entry entry = ref NextEntry(dictionary, ref _index);
            if (Unsafe.IsNullRef(ref entry))
            {
                Current = default!;
                return false;
            }

            Current = entry.Value;
            return true;
        }

        readonly object? IEnumerator.Current => Current;

        void IEnumerator.Reset()
        {
            _index = 0;
            Current = default!;
        }

        public readonly void Dispose() { }
    }
}
