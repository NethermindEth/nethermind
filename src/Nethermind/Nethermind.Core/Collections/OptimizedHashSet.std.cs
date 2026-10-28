// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash set for hot state-tracking paths. Standard execution is a plain <see cref="HashSet{T}"/>;
/// the zkVM build provides its own implementation tuned for the guest's cost model.
/// </summary>
/// <remarks>Its items carry the key requirements of <see cref="OptimizedDictionary{TKey,TValue}"/>.</remarks>
public sealed class OptimizedHashSet<T> : HashSet<T> where T : notnull, IEquatable<T>
{
    private SparseClearTrimmer _trimmer;

    public OptimizedHashSet() { }

    public OptimizedHashSet(int capacity) : base(capacity) { }

    public OptimizedHashSet(IEqualityComparer<T>? comparer) : base(comparer) { }

    public OptimizedHashSet(int capacity, IEqualityComparer<T>? comparer) : base(capacity, comparer) { }

    /// <inheritdoc cref="OptimizedDictionary{TKey,TValue}.ClearAndTrim()"/>
    public void ClearAndTrim()
    {
        int count = Count;
        Clear();
        int capacity = Capacity;
        if (capacity > CollectionExtensions.DefaultTrimAboveCapacity)
        {
            TrimExcess(CollectionExtensions.DefaultTrimToCapacity);
            _trimmer.Reset();
            return;
        }

        int sparseTrimTo = _trimmer.OnClear(count, capacity);
        if (sparseTrimTo != 0) TrimExcess(sparseTrimTo);
    }

    /// <inheritdoc cref="OptimizedDictionary{TKey,TValue}.ClearAndTrim(int, int)"/>
    public void ClearAndTrim(int trimAboveCapacity, int trimToCapacity)
    {
        Clear();
        if (Capacity > trimAboveCapacity) TrimExcess(trimToCapacity);
    }
}
