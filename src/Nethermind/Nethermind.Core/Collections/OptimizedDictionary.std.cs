// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash map for hot state-tracking paths. Standard execution is a plain <see cref="Dictionary{TKey,TValue}"/>;
/// the zkVM build provides its own implementation tuned for the guest's cost model.
/// </summary>
/// <remarks>
/// The zkVM build accepts no custom comparer, indexes its buckets by the low bits of <see cref="object.GetHashCode"/>
/// and hashes keys in place, so a key type needs well-mixed hash bits and a <see cref="object.GetHashCode"/> that does
/// not write to the key.
/// </remarks>
public sealed class OptimizedDictionary<TKey, TValue> : Dictionary<TKey, TValue> where TKey : notnull, IEquatable<TKey>
{
    private SparseClearTrimmer _trimmer;

    public OptimizedDictionary() { }

    public OptimizedDictionary(int capacity) : base(capacity) { }

    public OptimizedDictionary(IEqualityComparer<TKey>? comparer) : base(comparer) { }

    public OptimizedDictionary(int capacity, IEqualityComparer<TKey>? comparer) : base(capacity, comparer) { }

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrNullRef{TKey,TValue}(Dictionary{TKey,TValue},TKey)"/>
    public ref TValue GetValueRefOrNullRef(TKey key) => ref CollectionsMarshal.GetValueRefOrNullRef(this, key);

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrAddDefault{TKey,TValue}(Dictionary{TKey,TValue},TKey,out bool)"/>
    public ref TValue? GetValueRefOrAddDefault(TKey key, out bool exists) => ref CollectionsMarshal.GetValueRefOrAddDefault(this, key, out exists);

    /// <summary>Clears the map and shrinks its storage once it exceeds <see cref="CollectionExtensions.DefaultTrimAboveCapacity"/>.</summary>
    /// <remarks>
    /// A map that stays sparse across many clears is also shrunk to fit them; see <see cref="SparseClearTrimmer"/>.
    /// Only an empty map is shrunk, and an empty map fills its slots from the first in insertion order whatever its
    /// capacity, so the shrink changes neither its contents nor its enumeration order.
    /// </remarks>
    public void ClearAndTrim()
    {
        int count = Count;
        Clear();
        int trimTo = _trimmer.OnClear(count, Capacity);
        if (trimTo != 0) TrimExcess(trimTo);
    }

    /// <summary>Clears the map and shrinks its storage to <paramref name="trimToCapacity"/> once it exceeds <paramref name="trimAboveCapacity"/>.</summary>
    /// <remarks>A map with its own capacity floor, such as a pooled one, is never shrunk below it for sparseness.</remarks>
    public void ClearAndTrim(int trimAboveCapacity, int trimToCapacity)
    {
        Clear();
        if (Capacity > trimAboveCapacity) TrimExcess(trimToCapacity);
    }
}
