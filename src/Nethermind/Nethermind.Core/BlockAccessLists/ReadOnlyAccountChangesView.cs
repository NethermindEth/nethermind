// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Address-sorted view over <see cref="ReadOnlyBlockAccessList"/>'s accounts. Iteration via
/// <c>foreach</c> dispatches to a <see cref="ReadOnlySpan{T}"/> enumerator (zero allocations);
/// <see cref="IEnumerable{T}"/> usage still works but boxes its enumerator, so prefer the
/// pattern-based form on hot paths.
/// </summary>
public readonly struct ReadOnlyAccountChangesView : IEnumerable<ReadOnlyAccountChanges>
{
    private readonly ReadOnlyAccountChanges[] _items;

    internal ReadOnlyAccountChangesView(ReadOnlyAccountChanges[]? items) => _items = items ?? [];

    public int Count => _items.Length;

    public ReadOnlySpan<ReadOnlyAccountChanges> AsSpan() => _items;

    public ReadOnlySpan<ReadOnlyAccountChanges>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();

    IEnumerator<ReadOnlyAccountChanges> IEnumerable<ReadOnlyAccountChanges>.GetEnumerator()
        => ((IEnumerable<ReadOnlyAccountChanges>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
