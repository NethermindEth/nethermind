// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash set with the <see cref="HashSet{T}"/> members its callers use, tuned for the zkVM guest.
/// </summary>
/// <remarks>
/// Keeps <see cref="HashSet{T}"/>'s layout and algorithm, so enumeration order and the free list behave the same, but
/// every field, bucket and entry link a probe reads is a 64-bit word: the guest's emulator charges about six times as
/// much for a 32-bit load or store as for an aligned 64-bit one. Buckets are a power of two indexed by a multiplicative
/// hash rather than a prime with a fast-modulo reduction, and the tables are indexed without bounds checks. As in
/// <see cref="HashSet{T}"/>, each probe loop spells out the item comparison: a helper taking the items by value would
/// copy a struct item per probe.
/// <para>
/// Removed and cleared entries keep their contents: nothing reads an entry past the count or on the free list, and the
/// guest has no garbage collector for them to hold objects from. Single-threaded, and an enumerator does not detect a
/// modification made while it runs.
/// </para>
/// </remarks>
public sealed class OptimizedHashSet<T> : IReadOnlyCollection<T>
{
    // Mirrors HashSet's encoding: a free entry's Next is StartOfFreeList minus the next free index, so live entries
    // are the ones with Next >= -1.
    private const long StartOfFreeList = -3;

    private struct Entry
    {
        public ulong HashCode;
        public long Next;
        public T Value;
    }

    private readonly IEqualityComparer<T>? _comparer;
    // One-based entry index per bucket; zero marks an empty bucket.
    private long[]? _buckets;
    private Entry[] _entries = [];
    private long _size;
    private long _shift;
    private long _count;
    private long _freeList = -1;
    private long _freeCount;

    public OptimizedHashSet() : this(0, null) { }

    public OptimizedHashSet(int capacity) : this(capacity, null) { }

    public OptimizedHashSet(IEqualityComparer<T>? comparer) : this(0, comparer) { }

    public OptimizedHashSet(int capacity, IEqualityComparer<T>? comparer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > 0) Initialize(capacity);
        _comparer = OptimizedHashing.ResolveComparer(comparer);
    }

    public int Count => (int)(_count - _freeCount);

    /// <summary>The number of items the set holds before it grows.</summary>
    public int Capacity => (int)_size;

    public bool Contains(T item)
    {
        if (_buckets is not null)
        {
            IEqualityComparer<T>? comparer = _comparer;
            ulong hashCode = GetHashCode(comparer, item);
            Entry[] entries = _entries;
            long i = OptimizedHashing.At(_buckets, OptimizedHashing.BucketIndex(hashCode, _shift)) - 1;
            while (i >= 0)
            {
                ref Entry entry = ref OptimizedHashing.At(entries, i);
                if (entry.HashCode == hashCode
                    && (typeof(T).IsValueType && comparer is null
                        ? EqualityComparer<T>.Default.Equals(entry.Value, item)
                        : comparer!.Equals(entry.Value, item)))
                {
                    return true;
                }
                i = entry.Next;
            }
        }

        return false;
    }

    public bool Add(T item)
    {
        if (_buckets is null) Initialize(0);

        IEqualityComparer<T>? comparer = _comparer;
        ulong hashCode = GetHashCode(comparer, item);
        ref long bucket = ref OptimizedHashing.At(_buckets!, OptimizedHashing.BucketIndex(hashCode, _shift));
        Entry[] entries = _entries;
        long i = bucket - 1;
        while (i >= 0)
        {
            ref Entry entry = ref OptimizedHashing.At(entries, i);
            if (entry.HashCode == hashCode
                && (typeof(T).IsValueType && comparer is null
                    ? EqualityComparer<T>.Default.Equals(entry.Value, item)
                    : comparer!.Equals(entry.Value, item)))
            {
                return false;
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
        added.Value = item;
        bucket = index + 1;
        return true;
    }

    public bool Remove(T item)
    {
        if (_buckets is null) return false;

        IEqualityComparer<T>? comparer = _comparer;
        ulong hashCode = GetHashCode(comparer, item);
        ref long bucket = ref OptimizedHashing.At(_buckets, OptimizedHashing.BucketIndex(hashCode, _shift));
        Entry[] entries = _entries;
        long last = -1;
        long i = bucket - 1;
        while (i >= 0)
        {
            ref Entry entry = ref OptimizedHashing.At(entries, i);
            if (entry.HashCode == hashCode
                && (typeof(T).IsValueType && comparer is null
                    ? EqualityComparer<T>.Default.Equals(entry.Value, item)
                    : comparer!.Equals(entry.Value, item)))
            {
                if (last < 0) bucket = entry.Next + 1;
                else OptimizedHashing.At(entries, last).Next = entry.Next;

                entry.Next = StartOfFreeList - _freeList;
                _freeList = i;
                _freeCount++;
                return true;
            }

            last = i;
            i = entry.Next;
        }

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

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetHashCode(IEqualityComparer<T>? comparer, T item) =>
        item is null
            ? 0
            : (uint)(typeof(T).IsValueType && comparer is null
                ? EqualityComparer<T>.Default.GetHashCode(item)
                : comparer!.GetHashCode(item));


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

    /// <summary>Enumerates the items in <see cref="HashSet{T}"/>'s order.</summary>
    public struct Enumerator(OptimizedHashSet<T> set) : IEnumerator<T>
    {
        private long _index;

        public T Current { readonly get; private set; } = default!;

        public bool MoveNext()
        {
            Entry[] entries = set._entries;
            long count = set._count;
            while (_index < count)
            {
                ref Entry entry = ref OptimizedHashing.At(entries, _index++);
                if (entry.Next >= -1)
                {
                    Current = entry.Value;
                    return true;
                }
            }

            Current = default!;
            return false;
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
