// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Resettables;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash map for hot state-tracking paths. Standard execution is a plain <see cref="Dictionary{TKey,TValue}"/>;
/// the zkVM build provides its own implementation tuned for the guest's cost model.
/// </summary>
/// <remarks>
/// Keeps <see cref="Dictionary{TKey,TValue}"/> semantics, including enumeration in insertion order with removed
/// slots reused last-freed-first, so host and guest observe the same order. Not thread-safe.
/// <para>
/// Keys hash and compare through their own <see cref="object.GetHashCode"/> and <see cref="IEquatable{T}"/> members,
/// taken by reference and called on the stored key in place, so a lookup copies no key and needs no zeroed stack frame;
/// a key's <see cref="object.GetHashCode"/> must therefore not write to the key.
/// A comparer is accepted only when it means exactly that.
/// The guest charges a narrow memory access about six times an aligned 64-bit one, so every bookkeeping field and
/// bucket is 64-bit, and the power-of-two bucket count is indexed by the low hash bits rather than a modulo, which
/// relies on the key hashes being mixed, as the state keys' are.
/// </para>
/// </remarks>
public sealed class OptimizedDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : IEquatable<TKey>
{
    private const long StartOfFreeList = -3;
    private const int MinCapacity = 4;
    private const int SparseClearShift = 3;

    private Entry[] _entries = [];
    // 1-based entry index of each chain head; 0 is an empty bucket.
    private long[] _buckets = [];
    private long _capacity;
    private long _count;
    private long _freeList = -1;
    private long _freeCount;
    private long _bucketMask;

    public OptimizedDictionary() : this(0, null) { }

    public OptimizedDictionary(int capacity) : this(capacity, null) { }

    public OptimizedDictionary(IEqualityComparer<TKey>? comparer) : this(0, comparer) { }

    public OptimizedDictionary(int capacity, IEqualityComparer<TKey>? comparer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (comparer is not null and not GenericEqualityComparer.IGenericEqualityComparer && !ReferenceEquals(comparer, EqualityComparer<TKey>.Default))
        {
            ThrowCustomComparer();
        }

        if (capacity > 0) Allocate(SizeFor(capacity));
    }

    public int Count => (int)(_count - _freeCount);

    public int Capacity => (int)_capacity;

    /// <summary>The equality the map applies, which is always the key's own.</summary>
    public IEqualityComparer<TKey> Comparer => EqualityComparer<TKey>.Default;

    public KeyCollection Keys => new(this);

    public ValueCollection Values => new(this);

    public TValue this[in TKey key]
    {
        get
        {
            ref TValue value = ref GetValueRefOrNullRef(key);
            if (Unsafe.IsNullRef(ref value)) ThrowKeyNotFound(key);
            return value;
        }
        set => GetValueRefOrAddDefault(key, out _) = value;
    }

    public bool ContainsKey(in TKey key) => !Unsafe.IsNullRef(ref GetValueRefOrNullRef(key));

    public bool TryGetValue(in TKey key, [MaybeNullWhen(false)] out TValue value)
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

    public void Add(in TKey key, TValue value)
    {
        if (!TryAdd(key, value)) ThrowDuplicateKey(key);
    }

    public bool TryAdd(in TKey key, TValue value)
    {
        ref TValue? slot = ref GetValueRefOrAddDefault(key, out bool exists);
        if (exists) return false;
        slot = value;
        return true;
    }

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrNullRef{TKey,TValue}(Dictionary{TKey,TValue},TKey)"/>
    public ref TValue GetValueRefOrNullRef(scoped in TKey key)
    {
        if (_count != 0)
        {
            ulong hashCode = GetHashCode(in key);
            Entry[] entries = _entries;
            long i = BucketAt(hashCode) - 1;
            while (i >= 0)
            {
                ref Entry entry = ref At(entries, i);
                if (entry.HashCode == hashCode && KeyEquals(ref entry.Key, in key)) return ref entry.Value;
                i = entry.Next;
            }
        }

        return ref Unsafe.NullRef<TValue>();
    }

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrAddDefault{TKey,TValue}(Dictionary{TKey,TValue},TKey,out bool)"/>
    public ref TValue? GetValueRefOrAddDefault(scoped in TKey key, out bool exists)
    {
        ulong hashCode = GetHashCode(in key);
        if (_count != 0)
        {
            Entry[] entries = _entries;
            long i = BucketAt(hashCode) - 1;
            while (i >= 0)
            {
                ref Entry entry = ref At(entries, i);
                if (entry.HashCode == hashCode && KeyEquals(ref entry.Key, in key))
                {
                    exists = true;
                    return ref entry.Value!;
                }
                i = entry.Next;
            }
        }

        long index;
        if (_freeCount > 0)
        {
            index = _freeList;
            _freeList = StartOfFreeList - At(_entries, index).Next;
            _freeCount--;
        }
        else
        {
            if (_count == _capacity) Resize(Math.Max((int)_capacity * 2, MinCapacity));
            index = _count++;
        }

        ref long bucket = ref BucketRef(hashCode);
        ref Entry added = ref At(_entries, index);
        added.HashCode = hashCode;
        added.Next = bucket - 1;
        added.Key = key;
        added.Value = default!;
        bucket = index + 1;

        exists = false;
        return ref added.Value!;
    }

    public bool Remove(in TKey key) => Remove(key, out _);

    public bool Remove(in TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_count != 0)
        {
            ulong hashCode = GetHashCode(in key);
            Entry[] entries = _entries;
            ref long bucket = ref BucketRef(hashCode);
            long last = -1;
            long i = bucket - 1;
            while (i >= 0)
            {
                ref Entry entry = ref At(entries, i);
                if (entry.HashCode == hashCode && KeyEquals(ref entry.Key, in key))
                {
                    if (last < 0) bucket = entry.Next + 1;
                    else At(entries, last).Next = entry.Next;

                    value = entry.Value;
                    entry.Next = StartOfFreeList - _freeList;
                    entry.Key = default!;
                    entry.Value = default!;
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

    /// <remarks>
    /// Leaves the entries as they are, since nothing reads a slot before an insert rewrites it and the guest has no
    /// collector for stale references to keep alive. A map holding few entries for its size zeroes only their buckets.
    /// </remarks>
    public void Clear()
    {
        if (_count == 0) return;
        if (_count <= _capacity >> SparseClearShift)
        {
            Entry[] entries = _entries;
            for (long i = 0; i < _count; i++) BucketRef(At(entries, i).HashCode) = 0;
        }
        else
        {
            Array.Clear(_buckets);
        }

        _count = 0;
        _freeList = -1;
        _freeCount = 0;
    }

    /// <summary>Clears the map and shrinks its storage to <paramref name="trimToCapacity"/> once it exceeds <paramref name="trimAboveCapacity"/>.</summary>
    /// <remarks>
    /// The only way to shrink the guest map: trimming a non-empty one would compact it at different sizes than
    /// <see cref="Dictionary{TKey,TValue}.TrimExcess(int)"/>, after which host and guest would reuse different slots.
    /// </remarks>
    public void ClearAndTrim(int trimAboveCapacity = CollectionExtensions.DefaultTrimAboveCapacity, int trimToCapacity = CollectionExtensions.DefaultTrimToCapacity)
    {
        Clear();
        if (_capacity > trimAboveCapacity)
        {
            int size = SizeFor(trimToCapacity);
            if (size < _capacity) Allocate(size);
        }
    }

    public int EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > _capacity) Resize(capacity);
        return Capacity;
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Hashing through a mutable reference stops the AOT compiler copying the key, and zeroing a frame for it, first.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetHashCode(in TKey key) => (uint)Unsafe.AsRef(in key).GetHashCode();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool KeyEquals(ref TKey stored, in TKey key) => stored.Equals(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long BucketAt(ulong hashCode) => BucketRef(hashCode);

    // Indices stay below the array lengths by construction: the bucket index is masked to the power-of-two bucket count,
    // and entry indices only come from buckets, chains and the free list, which hold slots below _count.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref long BucketRef(ulong hashCode) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buckets), (nint)(hashCode & (ulong)_bucketMask));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref Entry At(Entry[] entries, long index) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), (nint)index);

    private void Link(ref Entry entry, long index)
    {
        ref long bucket = ref BucketRef(entry.HashCode);
        entry.Next = bucket - 1;
        bucket = index + 1;
    }

    private void Allocate(int size)
    {
        _entries = new Entry[size];
        _buckets = new long[size];
        _capacity = size;
        _bucketMask = size - 1;
    }

    private void Resize(int capacity)
    {
        Entry[] oldEntries = _entries;
        Allocate(SizeFor(capacity));
        Array.Copy(oldEntries, _entries, (int)_count);
        Entry[] entries = _entries;
        for (long i = 0; i < _count; i++)
        {
            ref Entry entry = ref At(entries, i);
            if (entry.Next >= -1) Link(ref entry, i);
        }
    }

    private static int SizeFor(int capacity) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, MinCapacity));

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowCustomComparer() =>
        throw new NotSupportedException($"{nameof(OptimizedDictionary<,>)} uses the key's own equality; a custom comparer is not supported.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowKeyNotFound(TKey key) => throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowDuplicateKey(TKey key) => throw new ArgumentException($"An item with the same key has already been added. Key: {key}");

    internal struct Entry
    {
        public ulong HashCode;
        // Next entry in the chain (-1 ends it); below -1 the slot is free and encodes the next free slot.
        public long Next;
        public TKey Key;
        public TValue Value;
    }

    /// <remarks>Holds the position only and reads the entry on demand, so a large value is copied once, by the caller.</remarks>
    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly OptimizedDictionary<TKey, TValue> _dictionary;
        private long _index;
        private long _current;

        internal Enumerator(OptimizedDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
            _index = 0;
            _current = -1;
        }

        public readonly KeyValuePair<TKey, TValue> Current
        {
            get
            {
                if (_current < 0) return default;
                ref Entry entry = ref CurrentEntry;
                return new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
            }
        }

        readonly object IEnumerator.Current => Current;

        internal readonly ref Entry CurrentEntry => ref At(_dictionary._entries, _current);

        public bool MoveNext()
        {
            while (_index < _dictionary._count)
            {
                long index = _index++;
                if (At(_dictionary._entries, index).Next >= -1)
                {
                    _current = index;
                    return true;
                }
            }

            _current = -1;
            return false;
        }

        public void Reset()
        {
            _index = 0;
            _current = -1;
        }

        public readonly void Dispose() { }
    }

    public readonly struct KeyCollection(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerable<TKey>
    {
        public int Count => dictionary.Count;

        public KeyEnumerator GetEnumerator() => new(dictionary);

        IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public readonly struct ValueCollection(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerable<TValue>
    {
        public int Count => dictionary.Count;

        public ValueEnumerator GetEnumerator() => new(dictionary);

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <remarks>Unlike the BCL's, <see cref="Current"/> is valid only after <see cref="MoveNext"/> returns <see langword="true"/>.</remarks>
    public struct KeyEnumerator(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerator<TKey>
    {
        private Enumerator _inner = new(dictionary);

        public readonly TKey Current => _inner.CurrentEntry.Key;

        readonly object IEnumerator.Current => Current!;

        public bool MoveNext() => _inner.MoveNext();

        public void Reset() => _inner.Reset();

        public readonly void Dispose() { }
    }

    /// <remarks>Unlike the BCL's, <see cref="Current"/> is valid only after <see cref="MoveNext"/> returns <see langword="true"/>.</remarks>
    public struct ValueEnumerator(OptimizedDictionary<TKey, TValue> dictionary) : IEnumerator<TValue>
    {
        private Enumerator _inner = new(dictionary);

        public readonly TValue Current => _inner.CurrentEntry.Value;

        readonly object IEnumerator.Current => Current!;

        public bool MoveNext() => _inner.MoveNext();

        public void Reset() => _inner.Reset();

        public readonly void Dispose() { }
    }
}

public static class OptimizedDictionaryExtensions
{
    /// <inheritdoc cref="DictionaryExtensions.ResetAndClear{TKey,TValue}(IDictionary{TKey,TValue})"/>
    public static void ResetAndClear<TKey, TValue>(this OptimizedDictionary<TKey, TValue> dictionary)
        where TKey : IEquatable<TKey>
        where TValue : class, IReturnable
    {
        foreach (TValue value in dictionary.Values)
        {
            value.Return();
        }
        dictionary.Clear();
    }
}
