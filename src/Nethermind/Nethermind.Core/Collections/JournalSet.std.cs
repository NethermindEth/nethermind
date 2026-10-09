// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

public sealed partial class JournalSet<T> where T : notnull, IEquatable<T>
{
    // Removing entries one by one beats zeroing every bucket only while few remain: add, restore, clear and reuse
    // cycles on Address and StorageCell journals put the crossover between about Capacity/100 and Capacity/1000.
    private const int SparseClearCapacityDivisor = 256;

    public partial void Clear()
    {
        if (Count <= _set.Capacity / SparseClearCapacityDivisor)
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
}
