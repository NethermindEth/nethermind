// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Resettables;

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
    // Removing an entry costs a lookup, so it beats zeroing the buckets only while the map is this sparse.
    private const int SparseClearShift = 7;

    public OptimizedDictionary() { }

    public OptimizedDictionary(int capacity) : base(capacity) { }

    public OptimizedDictionary(IEqualityComparer<TKey>? comparer) : base(comparer) { }

    public OptimizedDictionary(int capacity, IEqualityComparer<TKey>? comparer) : base(capacity, comparer) { }

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrNullRef{TKey,TValue}(Dictionary{TKey,TValue},TKey)"/>
    public ref TValue GetValueRefOrNullRef(TKey key) => ref CollectionsMarshal.GetValueRefOrNullRef(this, key);

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrAddDefault{TKey,TValue}(Dictionary{TKey,TValue},TKey,out bool)"/>
    public ref TValue? GetValueRefOrAddDefault(TKey key, out bool exists) => ref CollectionsMarshal.GetValueRefOrAddDefault(this, key, out exists);

    /// <summary>Removes every entry; a map holding few entries for its capacity removes them one by one.</summary>
    /// <remarks>
    /// <see cref="Dictionary{TKey,TValue}.Clear"/> zeroes the whole bucket array, which a large pooled map pays in full on every
    /// reset even when it holds a handful of entries, as a call's state does. The guest map clears a sparse map the same way.
    /// </remarks>
    public new void Clear()
    {
        if (Count > Capacity >> SparseClearShift)
        {
            base.Clear();
            return;
        }

        foreach (TKey key in Keys)
        {
            Remove(key);
        }
    }
}

public static class OptimizedDictionaryExtensions
{
    /// <inheritdoc cref="DictionaryExtensions.ResetAndClear{TKey,TValue}(Dictionary{TKey,TValue})"/>
    public static void ResetAndClear<TKey, TValue>(this OptimizedDictionary<TKey, TValue> dictionary)
        where TKey : notnull, IEquatable<TKey>
        where TValue : class, IReturnable
    {
        foreach (TValue value in dictionary.Values)
        {
            value.Return();
        }
        dictionary.Clear();
    }
}
