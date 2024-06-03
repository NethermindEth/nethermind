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
        protected readonly Dictionary<TGroupKey, SortedSet<TValue>> _buckets;

        private readonly Dictionary<TKey, TValue> _cacheMap;
        private bool _isFull = false;

        // comparer for worst elements in buckets
        private readonly IComparer<TValue> _sortedComparer;

        // worst element from every group, used to determine element that will be evicted when pool is full
        private readonly DictionarySortedSet<TValue, TKey> _worstSortedValues;
        private KeyValuePair<TValue, TKey>? _worstValue = null;
        protected KeyValuePair<TValue, TKey>? WorstValue => _worstValue;
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
            _buckets = new Dictionary<TGroupKey, SortedSet<TValue>>();
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
            foreach (KeyValuePair<TGroupKey, SortedSet<TValue>> bucket in _buckets)
            {
                count += bucket.Value.Count;
            }

            snapshot = new TValue[count];
            var index = 0;
            foreach (KeyValuePair<TGroupKey, SortedSet<TValue>> bucket in _buckets)
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
        public Dictionary<TGroupKey, TValue[]> GetBucketSnapshot(Predicate<TGroupKey>? where = null)
        {
            using var lockRelease = Lock.Acquire();

            IEnumerable<KeyValuePair<TGroupKey, SortedSet<TValue>>> buckets = _buckets;
            if (where is not null)
            {
                buckets = buckets.Where(kvp => where(kvp.Key));
            }
            return buckets.ToDictionary(g => g.Key, g => g.Value.ToArray());
        }

        /// <summary>
        /// Gets all items of requested group.
        /// </summary>
        public TValue[] GetBucketSnapshot(TGroupKey group)
        {
            using var lockRelease = Lock.Acquire();

            if (group is null) throw new ArgumentNullException(nameof(group));
            return _buckets.TryGetValue(group, out SortedSet<TValue>? bucket) ? bucket.ToArray() : Array.Empty<TValue>();
        }

        /// <summary>
        /// Gets number of items in requested group.
        /// </summary>
        public int GetBucketCount(TGroupKey group)
        {
            using var lockRelease = Lock.Acquire();

            if (group is null) throw new ArgumentNullException(nameof(group));
            return _buckets.TryGetValue(group, out SortedSet<TValue>? bucket) ? bucket.Count : 0;
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
        public SortedSet<TValue> GetFirsts()
        {
            using var lockRelease = Lock.Acquire();

            SortedSet<TValue> sortedValues = new(_sortedComparer);
            foreach (KeyValuePair<TGroupKey, SortedSet<TValue>> bucket in _buckets)
            {
                sortedValues.Add(bucket.Value.Min!);
            }

            return sortedValues;
        }

        /// <summary>
        /// Gets last element in supplied comparer order.
        /// </summary>
        public bool TryGetLast(out TValue? last)
        {
            last = _worstValue.GetValueOrDefault().Key;
            return last is not null;
        }

        protected void WorstValuesAdd(TValue value)
        {
            _worstSortedValues.Add(value, GetKey(value));
            UpdateWorstValue();
        }

        protected void WorstValuesRemove(TValue value)
        {
            _worstSortedValues.Remove(value);
            UpdateWorstValue();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateWorstValue() =>
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
            if (_cacheMap.TryGetValue(key, out value) && value is not null)
            {
                if (Remove(key, value))
                {
                    TGroupKey groupMapping = MapToGroup(value);
                    if (_buckets.TryGetValue(groupMapping, out SortedSet<TValue>? bucketSet))
                    {
                        bucket = bucketSet;
                        if (bucketSet.Remove(value!))
                        {
                            if (bucket.Count == 0)
                            {
                                _buckets.Remove(groupMapping);
                            }
                            _snapshot = null;
                            return true;
                        }
                    }

                    Removed?.Invoke(this, new SortedPoolRemovedEventArgs(key, value, groupMapping, evicted));
                }
            }

            value = default;
            bucket = null;
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

            if (_buckets.TryGetValue(groupKey, out SortedSet<TValue>? bucket))
            {
                using SortedSet<TValue>.Enumerator enumerator = bucket!.GetEnumerator();
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
            return Enumerable.Empty<TValue>();
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
        public virtual bool TryGetValue(TKey key, [NotNullWhen(true)] out TValue? value)
        {
            using var lockRelease = Lock.Acquire();

            return _cacheMap.TryGetValue(key, out value) && value is not null;
        }

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
            return TryInsertNotLocked(key, value, out removed);
        }

        protected bool TryInsertNotLocked(TKey key, TValue value, out TValue? removed)
        {
            if (CanInsert(key, value))
            {
                TGroupKey group = MapToGroup(value);

                if (group is not null)
                {
                    InsertCore(key, value, group);

                    if (_cacheMap.Count > _capacity)
                    {
                        if (!RemoveLast(out removed) || _cacheMap.Count > _capacity)
                        {
                            if (_cacheMap.Count > _capacity && _logger.IsWarn)
                                _logger.Warn($"Capacity exceeded or failed to remove the last item from the pool, the current state is {Count}/{_capacity}. {GetInfoAboutWorstValues()}");
                            UpdateWorstValue();
                            RemoveLast(out removed);
                        }

                        return true;
                    }

                    removed = default;
                    return true;
                }
            }

            removed = default;
            return false;
        }

        public bool TryInsert(TKey key, TValue value) => TryInsert(key, value, out _);

        private bool RemoveLast(out TValue? removed)
        {
            TKey? key = _worstValue.GetValueOrDefault().Value;
            if (key is not null)
            {
                if (!TryRemoveNonLocked(key, true, out removed, out _))
                {
                    WorstValuesRemove(removed!);
                    return false;
                }
                return true;
            }
            else
            {
                removed = default;
                return false;
            }
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
        protected virtual void InsertCore(TKey key, TValue value, TGroupKey groupKey)
        {
            if (!_buckets.TryGetValue(groupKey, out SortedSet<TValue>? bucket))
            {
                _buckets[groupKey] = bucket = new SortedSet<TValue>(_groupComparer);
            }

            if (bucket.Add(value))
            {
                _cacheMap[key] = value;
                WorstValuesAdd(value);
                UpdateIsFull();
                _snapshot = null;
                Inserted?.Invoke(this, new SortedPoolEventArgs(key, value, groupKey));
            }
        }

        /// <summary>
        /// Actual removal mechanism.
        /// </summary>
        protected virtual bool Remove(TKey key, TValue value)
        {
            WorstValuesRemove(value);
            // Now remove from cache
            if (_cacheMap.Remove(key))
            {
                UpdateIsFull();
                _snapshot = null;
                return true;
            }

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

            if (_buckets.TryGetValue(groupKey, out SortedSet<TValue>? bucket))
            {
                items = bucket.ToArray();
                return true;
            }

            items = Array.Empty<TValue>();
            return false;
        }

        protected bool TryGetBucketsWorstValueNotLocked(TGroupKey groupKey, out TValue? item)
        {
            if (_buckets.TryGetValue(groupKey, out SortedSet<TValue>? bucket))
            {
                item = bucket.Max;
                return true;
            }

            item = default;
            return false;
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
            return $"Number of items in worstSortedValues: {_worstSortedValues.Count}; IsWorstValueInPool: {isWorstValueInPool}; Worst value: {_worstValue}; GetValue: {_worstValue.GetValueOrDefault()}; Current max in worstSortedValues: {_worstSortedValues.Max};";
        }

        public void UpdatePool(Func<TGroupKey, SortedSet<TValue>, IEnumerable<(TValue Tx, Action<TValue>? Change)>> changingElements)
        {
            using var lockRelease = Lock.Acquire();

            foreach ((TGroupKey groupKey, SortedSet<TValue> bucket) in _buckets)
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroupNotLocked(groupKey, bucket, changingElements);
            }
        }

        public void UpdateGroup(TGroupKey groupKey, Func<TGroupKey, SortedSet<TValue>, IEnumerable<(TValue Tx, Action<TValue>? Change)>> changingElements)
        {
            using var lockRelease = Lock.Acquire();

            if (groupKey is null) throw new ArgumentNullException(nameof(groupKey));
            if (_buckets.TryGetValue(groupKey, out SortedSet<TValue>? bucket))
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroupNotLocked(groupKey, bucket, changingElements);
            }
        }

        protected virtual void UpdateGroupNotLocked(TGroupKey groupKey, SortedSet<TValue> bucket, Func<TGroupKey, SortedSet<TValue>, IEnumerable<(TValue Tx, Action<TValue>? Change)>> changingElements)
        {
            foreach ((TValue value, Action<TValue>? change) in changingElements(groupKey, bucket))
            {
                change?.Invoke(value);
            }
        }

        protected virtual string ShortPoolName => "SortedPool";
    }
}
