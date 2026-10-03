// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash set with the <see cref="HashSet{T}"/> surface the state path uses.
/// </summary>
/// <remarks>
/// The guest build is <see cref="OptimizedHashTable{TKey,TValue}"/>: word-sized fields and links, a power-of-two bucket
/// array and direct hash and equality calls for value-type items. Enumeration order matches <see cref="HashSet{T}"/>.
/// </remarks>
public sealed class OptimizedHashSet<T>(int capacity, IEqualityComparer<T>? comparer) : ICollection<T>, IReadOnlyCollection<T>
{
    private OptimizedHashTable<T, NoValue> _table = new(capacity, comparer);

    public OptimizedHashSet() : this(0, null) { }

    public OptimizedHashSet(int capacity) : this(capacity, null) { }

    public OptimizedHashSet(IEqualityComparer<T>? comparer) : this(0, comparer) { }

    public int Count => _table.Count;

    public int Capacity => _table.Capacity;

    public IEqualityComparer<T> Comparer => _table.Comparer;

    public bool IsReadOnly => false;

    /// <returns><see langword="true"/> when <paramref name="item"/> was not in the set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in T item)
    {
        _table.FindOrAdd(in item, out bool exists);
        return !exists;
    }

    void ICollection<T>.Add(T item) => Add(in item);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(in T item) => !Unsafe.IsNullRef(ref _table.FindEntry(in item));

    bool ICollection<T>.Contains(T item) => Contains(in item);

    public bool Remove(T item) => _table.Remove(in item, out _);

    public void Clear() => _table.Clear();

    public int EnsureCapacity(int capacity) => _table.EnsureCapacity(capacity);

    public void TrimExcess() => _table.TrimExcess(Count);

    public void TrimExcess(int capacity) => _table.TrimExcess(capacity);

    /// <summary>Clears the set, shrinking it to <paramref name="trimToCapacity"/> when it grew past <paramref name="trimAboveCapacity"/>.</summary>
    public void ClearAndTrim(int trimAboveCapacity = CollectionExtensions.DefaultTrimAboveCapacity, int trimToCapacity = CollectionExtensions.DefaultTrimToCapacity)
    {
        Clear();
        if (Capacity > trimAboveCapacity) TrimExcess(trimToCapacity);
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        foreach (T item in this) array[arrayIndex++] = item;
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Walks the live items in storage order.</summary>
    public struct Enumerator(OptimizedHashSet<T> set) : IEnumerator<T>
    {
        private readonly OptimizedHashTable<T, NoValue>.Entry[] _entries = set._table.Entries;
        private readonly nint _used = set._table.Used;
        private nint _index = -1;

        public bool MoveNext()
        {
            while (++_index < _used)
            {
                if (OptimizedHashTable<T, NoValue>.IsLive(in CurrentEntry)) return true;
            }

            return false;
        }

        public readonly T Current => CurrentEntry.Key;

        private readonly ref OptimizedHashTable<T, NoValue>.Entry CurrentEntry =>
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), _index);

        readonly object IEnumerator.Current => Current!;

        public void Reset() => _index = -1;

        public readonly void Dispose() { }
    }

    private readonly struct NoValue;
}
