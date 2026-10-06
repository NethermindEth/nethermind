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
    public OptimizedDictionary() { }

    public OptimizedDictionary(int capacity) : base(capacity) { }

    public OptimizedDictionary(IEqualityComparer<TKey>? comparer) : base(comparer) { }

    public OptimizedDictionary(int capacity, IEqualityComparer<TKey>? comparer) : base(capacity, comparer) { }

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrNullRef{TKey,TValue}(Dictionary{TKey,TValue},TKey)"/>
    public ref TValue GetValueRefOrNullRef(TKey key) => ref CollectionsMarshal.GetValueRefOrNullRef(this, key);

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrAddDefault{TKey,TValue}(Dictionary{TKey,TValue},TKey,out bool)"/>
    public ref TValue? GetValueRefOrAddDefault(TKey key, out bool exists) => ref CollectionsMarshal.GetValueRefOrAddDefault(this, key, out exists);
}
