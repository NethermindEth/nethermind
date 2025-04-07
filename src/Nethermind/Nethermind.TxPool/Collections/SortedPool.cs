// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections
{
    /// <summary>
    /// Keeps a pool of <see cref="TValue"/> with <see cref="TKey"/> in groups based on <see cref="TGroupKey"/>.
    /// </summary>
    /// <typeparam name="TKey">Type of keys of items, unique in pool.</typeparam>
    /// <typeparam name="TValue">Type of items that are kept.</typeparam>
    /// <typeparam name="TGroupKey">Type of groups in which the items are organized</typeparam>
    public abstract partial class SortedPool<TKey, TValue, TGroupKey>
        where TKey : notnull
        where TGroupKey : notnull
    {
        protected McsPriorityLock Lock { get; } = new();

        private readonly int _capacity;
        private readonly ILogger _logger;

        // comparer for a bucket
        private readonly IComparer<TValue> _groupComparer;

        // group buckets, keep the items grouped by group key and sorted in group
        protected readonly Dictionary<TGroupKey, EnhancedSortedSet<TValue>> _buckets;

        private readonly Dictionary<TKey, TValue> _cacheMap;
        private bool _isFull = false;

        // comparer for worst elements in buckets
        private readonly IComparer<TValue> _sortedComparer;

        // worst element from every group, used to determine element that will be evicted when pool is full
        protected readonly DictionarySortedSet<TValue, TKey> _worstSortedValues;
        protected KeyValuePair<TValue, TKey>? _worstValue = null;
        private TValue[]? _snapshot;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="capacity">Max capacity, after surpassing it elements will be removed based on last by <see cref="comparer"/>.</param>
        /// <param name="comparer">Comparer to sort items.</param>
        protected SortedPool(int capacity, IComparer<TValue> comparer, ILogManager logManager)
        {
            _capacity = capacity;
            // ReSharper disable VirtualMemberCallInConstructor
            _sortedComparer = GetUniqueComparer(comparer ?? throw new ArgumentNullException(nameof(comparer)));
            _groupComparer = GetGroupComparer(comparer ?? throw new ArgumentNullException(nameof(comparer)));
            _cacheMap = new Dictionary<TKey, TValue>(); // do not initialize it at the full capacity
            _buckets = new Dictionary<TGroupKey, EnhancedSortedSet<TValue>>();
            _worstSortedValues = new DictionarySortedSet<TValue, TKey>(_sortedComparer);
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        /// <summary>
        /// Gets comparer that preserves original comparer order, but also differentiates on <see cref="TValue"/> items based on their identity.
        /// </summary>
        /// <param name="comparer">Original comparer.</param>
        /// <returns>Identity comparer.</returns>
        protected abstract IComparer<TValue> GetUniqueComparer(IComparer<TValue> comparer);

        /// <summary>
        /// Gets comparer for same group.
        /// </summary>
        /// <param name="comparer">Original comparer.</param>
        /// <returns>Group comparer.</returns>
        protected abstract IComparer<TValue> GetGroupComparer(IComparer<TValue> comparer);

        /// <summary>
        /// Maps item to group
        /// </summary>
        /// <param name="value">Item to map.</param>
        /// <returns>Mapped group.</returns>
        protected abstract TGroupKey MapToGroup(TValue value);

        public int Count => _cacheMap.Count;

        /// <summary>
        /// Gets all items in random order.
        /// </summary>
        public TValue[] GetSnapshot()
        {
            TValue[]? snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is not null)
            {
                return snapshot;
            }

            return GetSnapShotLocked();
        }

        private TValue[] GetSnapShotLocked()
        {
            using var handle = Lock.Acquire();

            TValue[]? snapshot = _snapshot;
            if (snapshot is not null)
            {
                return snapshot;
            }

            var count = 0;
            foreach (KeyValuePair<TGroupKey, EnhancedSortedSet<TValue>> bucket in _buckets)
            {
                count += bucket.Value.Count;
            }

            snapshot = new TValue[count];
            var index = 0;
            foreach (KeyValuePair<TGroupKey, EnhancedSortedSet<TValue>> bucket in _buckets)
            {
                foreach (TValue value in bucket.Value)
                {
                    snapshot[index] = value;
                    index++;
                }
            }

            _snapshot = snapshot;
            return snapshot;
        }

        /// <summary>
        /// Gets all items in groups in supplied comparer order in groups.
        /// </summary>
        public Dictionary<TGroupKey, TValue[]> GetBucketSnapshot(Predicate<(TGroupKey key, TValue first)>? where = null)
        {
            using var lockRelease = Lock.Acquire();

            IEnumerable<KeyValuePair<TGroupKey, EnhancedSortedSet<TValue>>> buckets = _buckets;
            if (where is not null)
            {
                buckets = buckets.Where(kvp => kvp.Value.Count > 0 && where.Invoke((kvp.Key, kvp.Value.Min!)));
            }
            return buckets.ToDictionary(g => g.Key, g => g.Value.ToArray());
        }

        /// <summary>
        /// Gets all items of requested group.
        /// </summary>
        public TValue[] GetBucketSnapshot(TGroupKey group)
        {
            using var lockRelease = Lock.Acquire();

            ArgumentNullException.ThrowIfNull(group);
            return _buckets.TryGetValue(group, out EnhancedSortedSet<TValue>? bucket) ? bucket.ToArray() : [];
        }

        /// <summary>
        /// Gets number of items in requested group.
        /// </summary>
        public int GetBucketCount(TGroupKey group)
        {
            using var lockRelease = Lock.Acquire();

            ArgumentNullException.ThrowIfNull(group);
            return _buckets.TryGetValue(group, out EnhancedSortedSet<TValue>? bucket) ? bucket.Count : 0;
        }

        /// <summary>
        /// Takes first element in supplied comparer order.
        /// </summary>
        public bool TryTakeFirst(out TValue? first)
        {
            if (GetFirsts().Min is TValue min)
                return TryRemove(GetKey(min), out first);
            first = default;
            return false;
        }

        /// <summary>
        /// Returns best element of each bucket in supplied comparer order.
        /// </summary>
        public EnhancedSortedSet<TValue> GetFirsts()
        {
            using var lockRelease = Lock.Acquire();

            EnhancedSortedSet<TValue> sortedValues = new(_sortedComparer);
            foreach (KeyValuePair<TGroupKey, EnhancedSortedSet<TValue>> bucket in _buckets)
            {
                sortedValues.Add(bucket.Value.Min!);
            }

            return sortedValues;
        }

        /// <summary>
        /// Returns best overall element as per supplied comparer order.
        /// </summary>
        public TValue? GetBest()
        {
            return GetFirsts().Min;
        }

        /// <summary>
        /// Gets last element in supplied comparer order.
        /// </summary>
        public bool TryGetLast(out TValue? last)
        {
            last = _worstValue.GetValueOrDefault().Key;
            return last is not null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void UpdateWorstValue() =>
            _worstValue = _worstSortedValues.Max;

        /// <summary>
        /// Tries to remove element.
        /// </summary>
        /// <param name="key">Key to be removed.</param>
        /// <param name="value">Removed element or null.</param>
        /// <param name="bucket">Bucket for same sender transactions.</param>
        /// <returns>If element was removed. False if element was not present in pool.</returns>
        private bool TryRemove(TKey key, out TValue? value, [NotNullWhen(true)] out ICollection<TValue>? bucket)
        {
            using var lockRelease = Lock.Acquire();

            return TryRemoveNonLocked(key, false, out value, out bucket);
        }

        protected bool TryRemoveNonLocked(TKey key, bool evicted, [NotNullWhen(true)] out TValue? value, out ICollection<TValue>? bucket)
        {
            if (Remove(key, out value) && value is not null)
            {
                if (RemoveFromBucket(value, out EnhancedSortedSet<TValue>? bucketSet))
                {
                    bucket = bucketSet;
                    Removed?.Invoke(this, new SortedPoolRemovedEventArgs(key, value, evicted));
                    return true;
                }

                // just for safety
                _worstSortedValues.Remove(value);
                UpdateWorstValue();
            }

            value = default;
            bucket = null;
            return false;
        }

        private bool RemoveFromBucket([DisallowNull] TValue value, out EnhancedSortedSet<TValue>? bucketSet)
        {
            TGroupKey groupMapping = MapToGroup(value);
            if (_buckets.TryGetValue(groupMapping, out bucketSet))
            {
                TValue? last = bucketSet.Max;
                if (bucketSet.Remove(value))
                {
                    if (bucketSet.Count == 0)
                    {
                        _buckets.Remove(groupMapping);
                        if (last is not null)
                        {
                            _worstSortedValues.Remove(last);
                            UpdateWorstValue();
                        }
                    }
                    else
                    {
                        UpdateSortedValues(bucketSet, last);
                    }

                    _snapshot = null;
                    return true;
                }
            }

            return false;
        }

        protected abstract TKey GetKey(TValue value);

        public bool TryRemove(TKey key, [NotNullWhen(true)] out TValue? value) => TryRemove(key, out value, out _);

        public bool TryRemove(TKey key) => TryRemove(key, out _, out _);

        /// <summary>
        /// Tries to get elements matching predicated criteria, iterating through SortedSet with break on first mismatch.
        /// </summary>
        /// <param name="groupKey">Given GroupKey, which elements are checked.</param>
        /// <param name="where">Predicated criteria.</param>
        /// <returns>Elements matching predicated criteria.</returns>
        public IEnumerable<TValue> TakeWhile(TGroupKey groupKey, Predicate<TValue> where)
        {
            using var lockRelease = Lock.Acquire();

            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<TValue>? bucket))
            {
                using EnhancedSortedSet<TValue>.Enumerator enumerator = bucket!.GetEnumerator();
                List<TValue>? list = null;

                while (enumerator.MoveNext())
                {
                    if (!where(enumerator.Current))
                    {
                        break;
                    }

                    list ??= new List<TValue>();
                    list.Add(enumerator.Current);
                }

                return list ?? Enumerable.Empty<TValue>();
            }
            return [];
        }

        /// <summary>
        /// Checks if element is present.
        /// </summary>
        /// <param name="key">Key to check presence.</param>
        /// <returns>True if element is present in pool.</returns>
        public bool ContainsKey(TKey key)
        {
            using var lockRelease = Lock.Acquire();

            return _cacheMap.ContainsKey(key);
        }

        /// <summary>
        /// Tries to get element.
        /// </summary>
        /// <param name="key">Key to be returned.</param>
        /// <param name="value">Returned element or null.</param>
        /// <returns>If element retrieval succeeded. True if element was present in pool.</returns>
        public bool TryGetValue(TKey key, [NotNullWhen(true)] out TValue? value)
        {
            using var lockRelease = Lock.Acquire();

            return TryGetValueNonLocked(key, out value);
        }

        protected virtual bool TryGetValueNonLocked(TKey key, [NotNullWhen(true)] out TValue? value) => _cacheMap.TryGetValue(key, out value) && value is not null;

        /// <summary>
        /// Tries to insert element.
        /// </summary>
        /// <param name="key">Key to be inserted.</param>
        /// <param name="value">Element to insert.</param>
        /// <param name="removed">Element removed because of exceeding capacity</param>
        /// <returns>If element was inserted. False if element was already present in pool.</returns>
        public bool TryInsert(TKey key, TValue value, out TValue? removed)
        {
            using var lockRelease = Lock.Acquire();

            if (CanInsert(key, value))
            {
                TGroupKey group = MapToGroup(value);

                if (group is not null)
                {
                    bool inserted = InsertCore(key, value, group);

                    if (_cacheMap.Count > _capacity)
                    {
                        if (!RemoveLast(out removed) || _cacheMap.Count > _capacity)
                        {
                            if (_cacheMap.Count > _capacity && _logger.IsWarn)
                                _logger.Warn($"Capacity exceeded or failed to remove the last item from the pool, the current state is {Count}/{_capacity}. {GetInfoAboutWorstValues()}");
                            UpdateWorstValue();
                            RemoveLast(out removed);
                        }

                        return inserted;
                    }

                    removed = default;
                    return inserted;
                }
            }

            removed = default;
            return false;
        }

        public bool TryInsert(TKey key, TValue value) => TryInsert(key, value, out _);

        private bool RemoveLast(out TValue? removed)
        {
        TryAgain:
            KeyValuePair<TValue, TKey> worstValue = _worstValue.GetValueOrDefault();
            TKey? key = worstValue.Value;
            if (key is not null)
            {
                if (TryRemoveNonLocked(key, true, out removed, out _))
                {
                    return true;
                }

                if (worstValue.Key is not null && _worstSortedValues.Remove(worstValue))
                {
                    RemoveFromBucket(worstValue.Key, out _);
                }
                else
                {
                    UpdateWorstValue();
                }

                goto TryAgain;
            }

            removed = default;
            return false;
        }

        /// <summary>
        /// Checks if element can be inserted.
        /// </summary>
        protected virtual bool CanInsert(TKey key, TValue value)
        {
            if (value is null)
            {
                throw new ArgumentNullException();
            }

            return !_cacheMap.ContainsKey(key);
        }

        /// <summary>
        /// Actual insert mechanism.
        /// </summary>
        protected virtual bool InsertCore(TKey key, TValue value, TGroupKey groupKey)
        {
            if (!_buckets.TryGetValue(groupKey, out EnhancedSortedSet<TValue>? bucket))
            {
                _buckets[groupKey] = bucket = new EnhancedSortedSet<TValue>(_groupComparer);
            }

            TValue? last = bucket.Max;
            if (bucket.Add(value))
            {
                _cacheMap[key] = value;
                UpdateIsFull();
                UpdateSortedValues(bucket, last);
                _snapshot = null;
                Inserted?.Invoke(this, new SortedPoolEventArgs(key, value));
                return true;
            }

            return false;
        }

        private void UpdateSortedValues(EnhancedSortedSet<TValue> bucket, TValue? previousLast)
        {
            TValue? newLast = bucket.Max;
            if (!Equals(previousLast, newLast))
            {
                if (previousLast is not null)
                {
                    _worstSortedValues.Remove(previousLast);
                }

                if (newLast is not null)
                {
                    _worstSortedValues.Add(newLast, GetKey(newLast));
                }

                UpdateWorstValue();
            }
        }

        /// <summary>
        /// Actual removal mechanism.
        /// </summary>
        protected virtual bool Remove(TKey key, out TValue? value)
        {
            // Now remove from cache
            if (_cacheMap.Remove(key, out value))
            {
                UpdateIsFull();
                _snapshot = null;
                return true;
            }

            value = default;
            return false;
        }

        public bool IsFull() => _isFull;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateIsFull() =>
            _isFull = _cacheMap.Count >= _capacity;

        public bool ContainsBucket(TGroupKey groupKey)
        {
            using var lockRelease = Lock.Acquire();

            return _buckets.ContainsKey(groupKey);
        }

        public bool TryGetBucket(TGroupKey groupKey, out TValue[] items)
        {
            using var lockRelease = Lock.Acquire();

            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<TValue>? bucket))
            {
                items = bucket.ToArray();
                return true;
            }

            items = [];
            return false;
        }

        public bool TryGetBucketsWorstValue(TGroupKey groupKey, out TValue? item)
        {
            using var lockRelease = Lock.Acquire();

            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<TValue>? bucket))
            {
                item = bucket.Max;
                return true;
            }

            item = default;
            return false;
        }

        public bool BucketAny(TGroupKey groupKey, Func<TValue, bool> predicate)
        {
            using var lockRelease = Lock.Acquire();
            return _buckets.TryGetValue(groupKey, out EnhancedSortedSet<TValue>? bucket)
                && bucket.Any(predicate);
        }

        protected void EnsureCapacity(int? expectedCapacity = null)
        {
            expectedCapacity ??= _capacity; // expectedCapacity is added for testing purpose. null should be used in production code
            if (Count <= expectedCapacity)
                return;

            if (_logger.IsWarn)
                _logger.Warn($"{ShortPoolName} exceeds the config size {Count}/{expectedCapacity}. Trying to repair the pool");

            // Trying to auto-recover TxPool. If this code is executed, it means that something is wrong with our TxPool logic.
            // However, auto-recover mitigates bad consequences of such bugs.
            int maxIterations = 10; // We don't want to add an infinite loop, so we can break after a few iterations.
            int iterations = 0;
            while (Count > expectedCapacity)
            {
                ++iterations;
                if (RemoveLast(out TValue? removed))
                {
                    if (_logger.IsInfo)
                        _logger.Info($"Removed the last item {removed} from the pool, the current state is {Count}/{expectedCapacity}. {GetInfoAboutWorstValues()}");
                }
                else
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"Failed to remove the last item from the pool, the current state is {Count}/{expectedCapacity}. {GetInfoAboutWorstValues()}");
                    UpdateWorstValue();
                }

                if (iterations >= maxIterations) break;
            }
        }

        private string GetInfoAboutWorstValues()
        {
            TKey? key = _worstValue.GetValueOrDefault().Value;
            var isWorstValueInPool = _cacheMap.TryGetValue(key, out TValue? value) && value is not null;
            return $"Number of items in worstSortedValues: {_worstSortedValues.Count}; IsWorstValueInPool: {isWorstValueInPool};";
        }

        public void UpdatePool(Func<TGroupKey, IReadOnlySortedSet<TValue>, IEnumerable<(TValue Tx, Action<TValue>? Change)>> changingElements)
        {
            using var lockRelease = Lock.Acquire();

            foreach ((TGroupKey groupKey, EnhancedSortedSet<TValue> bucket) in _buckets)
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroup(groupKey, bucket, changingElements);
            }
        }

        public void UpdateGroup(TGroupKey groupKey, Func<TGroupKey, IReadOnlySortedSet<TValue>, IEnumerable<(TValue Tx, Action<TValue>? Change)>> changingElements)
        {
            using var lockRelease = Lock.Acquire();

            ArgumentNullException.ThrowIfNull(groupKey);
            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<TValue>? bucket))
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroup(groupKey, bucket, changingElements);
            }
        }

        protected virtual void UpdateGroup(TGroupKey groupKey, EnhancedSortedSet<TValue> bucket, Func<TGroupKey, IReadOnlySortedSet<TValue>, IEnumerable<(TValue Tx, Action<TValue>? Change)>> changingElements)
        {
            foreach ((TValue value, Action<TValue>? change) in changingElements(groupKey, bucket))
            {
                change?.Invoke(value);
            }
        }

        protected virtual string ShortPoolName => "SortedPool";
    }
}
