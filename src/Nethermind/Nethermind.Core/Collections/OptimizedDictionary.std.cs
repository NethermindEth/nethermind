// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A <see cref="Dictionary{TKey,TValue}"/>; the zkVM guest build replaces it with a map tuned for the guest's cost model.
/// </summary>
public sealed class OptimizedDictionary<TKey, TValue> : Dictionary<TKey, TValue> where TKey : notnull
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
