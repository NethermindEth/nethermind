// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Append-only list specialised for the common "0 or 1 entry" case: the first element is stored
/// inline as a field and the spill <see cref="List{T}"/> is only allocated when a second element
/// is added.
/// </summary>
/// <remarks>
/// Intended to be stored as a field on a reference type so the inline slot has a stable address;
/// <see cref="AsSpan"/> returns a one-element span over that field for <c>Count == 1</c> via
/// <see cref="MemoryMarshal.CreateReadOnlySpan{T}(ref T, int)"/> without allocating a backing
/// array. This is read-only-by-convention — callers must not mutate through the returned span
/// (the inline slot is the canonical store while <c>Count &lt;= 1</c>).
/// </remarks>
internal struct InlineList<T> where T : struct
{
    private T _first;
    private List<T>? _rest;
    private int _count;

    public readonly int Count => _count;

    public readonly T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) ThrowOutOfRange();
            if (index == 0 && _rest is null) return _first;
            return _rest![index];
        }
    }

    public void Add(T item)
    {
        if (_count == 0)
        {
            _first = item;
            _count = 1;
            return;
        }
        if (_rest is null)
        {
            _rest = [_first, item];
            _count = 2;
            return;
        }
        _rest.Add(item);
        _count++;
    }

    /// <summary>
    /// Non-allocating read-only view over the current entries. For <c>Count == 1</c> the span
    /// is created directly over the inline field; for <c>Count &gt; 1</c> it wraps the spill
    /// list via <see cref="CollectionsMarshal.AsSpan{T}(List{T})"/>.
    /// </summary>
    [UnscopedRef]
    public ReadOnlySpan<T> AsSpan()
    {
        if (_rest is not null) return CollectionsMarshal.AsSpan(_rest);
        if (_count == 0) return [];
        return MemoryMarshal.CreateReadOnlySpan(ref _first, 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfRange() => throw new ArgumentOutOfRangeException("index");
}
