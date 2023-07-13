// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;

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
        private readonly int _capacity;

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
        protected SortedPool(int capacity, IComparer<TValue> comparer)
        {
            _capacity = capacity;
            // ReSharper disable VirtualMemberCallInConstructor
            _sortedComparer = GetUniqueComparer(comparer ?? throw new ArgumentNullException(nameof(comparer)));
            _groupComparer = GetGroupComparer(comparer ?? throw new ArgumentNullException(nameof(comparer)));
            _cacheMap = new Dictionary<TKey, TValue>(); // do not initialize it at the full capacity
            _buckets = new Dictionary<TGroupKey, EnhancedSortedSet<TValue>>();
            _worstSortedValues = new DictionarySortedSet<TValue, TKey>(_sortedComparer);
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
        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue[] GetSnapshot()
        {
            TValue[]? snapshot = _snapshot;
            snapshot ??= _snapshot = _buckets.SelectMany(b => b.Value).ToArray();

            return snapshot;
        }

        /// <summary>
        /// Gets all items in groups in supplied comparer order in groups.
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public IDictionary<TGroupKey, TValue[]> GetBucketSnapshot(Predicate<TGroupKey>? where = null)
        {
            IEnumerable<KeyValuePair<TGroupKey, EnhancedSortedSet<TValue>>> buckets = _buckets;
            if (where is not null)
            {
                buckets = buckets.Where(kvp => where(kvp.Key));
            }
            return buckets.ToDictionary(g => g.Key, g => g.Value.ToArray());
        }

        /// <summary>
        /// Gets all items of requested group.
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue[] GetBucketSnapshot(TGroupKey group)
        {
            if (group is null) throw new ArgumentNullException(nameof(group));
            return _buckets.TryGetValue(group, out EnhancedSortedSet<TValue>? bucket) ? bucket.ToArray() : Array.Empty<TValue>();
        }

        /// <summary>
        /// Gets number of items in requested group.
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public int GetBucketCount(TGroupKey group)
        {
            if (group is null) throw new ArgumentNullException(nameof(group));
            return _buckets.TryGetValue(group, out EnhancedSortedSet<TValue>? bucket) ? bucket.Count : 0;
        }

        /// <summary>
        /// Takes first element in supplied comparer order.
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
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
        [MethodImpl(MethodImplOptions.Synchronized)]
        public EnhancedSortedSet<TValue> GetFirsts()
        {
            EnhancedSortedSet<TValue> sortedValues = new(_sortedComparer);
            foreach (KeyValuePair<TGroupKey, EnhancedSortedSet<TValue>> bucket in _buckets)
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
        [MethodImpl(MethodImplOptions.Synchronized)]
        private bool TryRemove(TKey key, out TValue? value, [NotNullWhen(true)] out ICollection<TValue>? bucket) =>
            TryRemove(key, false, out value, out bucket);

        private bool TryRemove(TKey key, bool evicted, [NotNullWhen(true)] out TValue? value, out ICollection<TValue>? bucket)
        {
            if (_cacheMap.TryGetValue(key, out value) && value != null)
            {
                if (Remove(key, value))
                {
                    TGroupKey groupMapping = MapToGroup(value);
                    if (_buckets.TryGetValue(groupMapping, out EnhancedSortedSet<TValue>? bucketSet))
                    {
                        bucket = bucketSet;
                        TValue? last = bucketSet.Max;
                        if (bucketSet.Remove(value!))
                        {
                            if (bucket.Count == 0)
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

                    Removed?.Invoke(this, new SortedPoolRemovedEventArgs(key, value, groupMapping, evicted));
                }
            }

            value = default;
            bucket = null;
            return false;
        }

        protected abstract TKey GetKey(TValue value);

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryRemove(TKey key, [NotNullWhen(true)] out TValue? value) => TryRemove(key, out value, out _);

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryRemove(TKey key) => TryRemove(key, out _, out _);

        /// <summary>
        /// Tries to get elements matching predicated criteria, iterating through SortedSet with break on first mismatch.
        /// </summary>
        /// <param name="groupKey">Given GroupKey, which elements are checked.</param>
        /// <param name="where">Predicated criteria.</param>
        /// <returns>Elements matching predicated criteria.</returns>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public IEnumerable<TValue> TakeWhile(TGroupKey groupKey, Predicate<TValue> where)
        {
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
            return Enumerable.Empty<TValue>();
        }

        /// <summary>
        /// Tries to get element.
        /// </summary>
        /// <param name="key">Key to be returned.</param>
        /// <param name="value">Returned element or null.</param>
        /// <returns>If element retrieval succeeded. True if element was present in pool.</returns>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetValue(TKey key, [NotNullWhen(true)] out TValue? value)
        {
            return _cacheMap.TryGetValue(key, out value) && value != null;
        }

        /// <summary>
        /// Tries to insert element.
        /// </summary>
        /// <param name="key">Key to be inserted.</param>
        /// <param name="value">Element to insert.</param>
        /// <param name="removed">Element removed because of exceeding capacity</param>
        /// <returns>If element was inserted. False if element was already present in pool.</returns>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public virtual bool TryInsert(TKey key, TValue value, out TValue? removed)
        {
            if (CanInsert(key, value))
            {
                TGroupKey group = MapToGroup(value);

                if (group is not null)
                {
                    InsertCore(key, value, group);

                    if (_cacheMap.Count > _capacity)
                    {
                        RemoveLast(out removed);
                        return true;
                    }

                    removed = default;
                    return true;
                }
            }

            removed = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryInsert(TKey key, TValue value) => TryInsert(key, value, out _);

        private void RemoveLast(out TValue? removed)
        {
            TKey? key = _worstValue.GetValueOrDefault().Value;
            if (key is not null)
            {
                TryRemove(key, true, out removed, out _);
            }
            else
            {
                removed = default;
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
                Inserted?.Invoke(this, new SortedPoolEventArgs(key, value, groupKey));
            }
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
        protected virtual bool Remove(TKey key, TValue value)
        {
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


        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetBucket(TGroupKey groupKey, out TValue[] items)
        {
            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<TValue>? bucket))
            {
                items = bucket.ToArray();
                return true;
            }

            items = Array.Empty<TValue>();
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetBucketsWorstValue(TGroupKey groupKey, out TValue? item)
        {
            if (_buckets.TryGetValue(groupKey, out EnhancedSortedSet<TValue>? bucket))
            {
                item = bucket.Max;
                return true;
            }

            item = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdatePool(Func<TGroupKey, IReadOnlySortedSet<TValue>, IEnumerable<(TValue Tx, Action<TValue>? Change)>> changingElements)
        {
            foreach ((TGroupKey groupKey, EnhancedSortedSet<TValue> bucket) in _buckets)
            {
                Debug.Assert(bucket.Count > 0);

                UpdateGroup(groupKey, bucket, changingElements);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdateGroup(TGroupKey groupKey, Func<TGroupKey, IReadOnlySortedSet<TValue>, IEnumerable<(TValue Tx, Action<TValue>? Change)>> changingElements)
        {
            if (groupKey is null) throw new ArgumentNullException(nameof(groupKey));
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
    }
}
