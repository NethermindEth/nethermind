// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Caching;

public sealed class DisposingLruCache<TKey, TValue> : LruCache<TKey, TValue>
    where TKey : notnull
    where TValue : IDisposable
{
    public DisposingLruCache(int maxCapacity, int startCapacity, string name)
        : base(maxCapacity, startCapacity, name)
    {
    }

    public DisposingLruCache(int maxCapacity, string name)
        : base(maxCapacity, name)
    {
    }

    protected override void Evict(TValue value) => value.Dispose();
}
