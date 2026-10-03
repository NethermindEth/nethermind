// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A hash map with the <see cref="Dictionary{TKey,TValue}"/> surface the state path uses.
/// </summary>
/// <remarks>
/// Standard execution is the BCL <see cref="Dictionary{TKey,TValue}"/>; the zkVM build swaps in a table laid out for the guest.
/// </remarks>
public sealed class OptimizedDictionary<TKey, TValue> : Dictionary<TKey, TValue> where TKey : notnull
{
    public OptimizedDictionary() { }

    public OptimizedDictionary(int capacity) : base(capacity) { }

    public OptimizedDictionary(IEqualityComparer<TKey>? comparer) : base(comparer) { }

    public OptimizedDictionary(int capacity, IEqualityComparer<TKey>? comparer) : base(capacity, comparer) { }

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrAddDefault{TKey,TValue}(Dictionary{TKey,TValue},TKey,out bool)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue? GetValueRefOrAddDefault(TKey key, out bool exists) => ref CollectionsMarshal.GetValueRefOrAddDefault(this, key, out exists);

    /// <inheritdoc cref="CollectionsMarshal.GetValueRefOrNullRef{TKey,TValue}(Dictionary{TKey,TValue},TKey)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TValue GetValueRefOrNullRef(TKey key) => ref CollectionsMarshal.GetValueRefOrNullRef(this, key);
}
