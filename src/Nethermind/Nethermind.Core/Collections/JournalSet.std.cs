// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

public sealed partial class JournalSet<T> where T : notnull, IEquatable<T>
{
    public partial void Clear()
    {
        if (Count <= _set.Capacity / CollectionExtensions.SparseClearCapacityDivisor)
        {
            foreach (T item in CollectionsMarshal.AsSpan(_items))
            {
                _set.Remove(item);
            }
        }
        else
        {
            _set.Clear();
        }

        _items.Clear();
    }

    private partial void Truncate(int count) => CollectionsMarshal.SetCount(_items, count);

    private partial void AddItem(in T item) => _items.Add(item);
}
