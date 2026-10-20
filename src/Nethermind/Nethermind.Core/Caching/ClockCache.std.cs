// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching;

public sealed partial class ClockCache<TKey, TValue>(int maxCapacity, int? lockPartition = null, IEqualityComparer<TKey>? comparer = null) : ClockCacheBase<TKey>(maxCapacity)
    where TKey : struct, IEquatable<TKey>
{
    private readonly ConcurrentDictionary<TKey, LruCacheItem> _cacheMap = new(lockPartition ?? Collections.CollectionExtensions.LockPartitions, maxCapacity, GenericEqualityComparer.GetOptimized(comparer));
    private readonly McsLock _lock = new();
}
