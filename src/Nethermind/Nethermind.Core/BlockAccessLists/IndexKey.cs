// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Comparable wrapper around an <see cref="IIndexedChange.Index"/> value, used as the key for
/// <see cref="System.MemoryExtensions.BinarySearch{T, TComparable}(System.ReadOnlySpan{T}, TComparable)"/>
/// over an index-sorted span of <typeparamref name="T"/>. Avoids constructing a synthetic
/// <typeparamref name="T"/> just to drive the search. Comparison is prestate-aware so
/// <see cref="Eip7928Constants.PrestateIndex"/> sorts before every real index — matching the
/// ordering produced when prestate entries are prepended via <c>LoadPreStateBalance</c> et al.
/// </summary>
internal readonly struct IndexKey<T>(uint index) : IComparable<T> where T : struct, IIndexedChange
{
    public int CompareTo(T other) => PrestateAwareIndexComparer.CompareIndices(index, other.Index);
}
