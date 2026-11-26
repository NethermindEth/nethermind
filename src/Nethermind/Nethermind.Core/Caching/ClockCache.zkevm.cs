// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching;

public sealed partial class ClockCache<TKey, TValue>(int maxCapacity, int? lockPartition = null, IEqualityComparer<TKey>? comparer = null) : ClockCacheBase<TKey>(maxCapacity)
    where TKey : struct, IEquatable<TKey>
{
    private readonly int? _lockPartition = lockPartition;
    private readonly Dictionary<TKey, LruCacheItem> _cacheMap = new(maxCapacity, comparer ?? throw new ArgumentNullException(nameof(comparer)));
    private readonly MockLock _lock = new();
}
