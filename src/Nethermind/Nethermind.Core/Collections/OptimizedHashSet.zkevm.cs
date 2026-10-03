// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash set for hot state-tracking paths. Standard execution is a plain <see cref="HashSet{T}"/>;
/// the zkVM build provides its own implementation tuned for the guest's cost model.
/// </summary>
/// <remarks>A key-only view of <see cref="OptimizedDictionary{TKey,TValue}"/>, whose remarks describe the layout.</remarks>
public sealed class OptimizedHashSet<T>(int capacity, IEqualityComparer<T>? comparer) : IReadOnlyCollection<T> where T : IEquatable<T>
{
    private readonly OptimizedDictionary<T, NoValue> _map = new(capacity, comparer);

    public OptimizedHashSet() : this(0, null) { }

    public OptimizedHashSet(int capacity) : this(capacity, null) { }

    public OptimizedHashSet(IEqualityComparer<T>? comparer) : this(0, comparer) { }

    public int Count => _map.Count;

    public int Capacity => _map.Capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in T item)
    {
        _map.GetValueRefOrAddDefault(item, out bool exists);
        return !exists;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(in T item) => !Unsafe.IsNullRef(ref _map.GetValueRefOrNullRef(item));

    public bool Remove(in T item) => _map.Remove(item);

    public void Clear() => _map.Clear();

    /// <inheritdoc cref="OptimizedDictionary{TKey,TValue}.ClearAndTrim"/>
    public void ClearAndTrim(int trimAboveCapacity = CollectionExtensions.DefaultTrimAboveCapacity, int trimToCapacity = CollectionExtensions.DefaultTrimToCapacity)
        => _map.ClearAndTrim(trimAboveCapacity, trimToCapacity);

    public int EnsureCapacity(int capacity) => _map.EnsureCapacity(capacity);

    public OptimizedDictionary<T, NoValue>.KeyEnumerator GetEnumerator() => _map.Keys.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The empty value type the backing map stores per item.</summary>
    public readonly struct NoValue;
}
