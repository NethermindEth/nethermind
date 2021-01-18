﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
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
    public abstract class SortedPool<TKey, TValue, TGroupKey>
    {
        private readonly int _capacity;
        private readonly IComparer<TValue> _comparer;
        private readonly IDictionary<TGroupKey, ICollection<TValue>> _buckets;
        private readonly DictionarySortedSet<TValue, TKey> _sortedValues;
        private readonly IDictionary<TKey, TValue> _cacheMap;
        
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="capacity">Max capacity, after surpassing it elements will be removed based on last by <see cref="comparer"/>.</param>
        /// <param name="comparer">Comparer to sort items.</param>
        protected SortedPool(int capacity, IComparer<TValue> comparer)
        {
            _capacity = capacity;
            // ReSharper disable once VirtualMemberCallInConstructor
            _comparer = GetUniqueComparer(comparer ?? throw new ArgumentNullException(nameof(comparer)));
            _cacheMap = new Dictionary<TKey, TValue>(); // do not initialize it at the full capacity
            _buckets = new Dictionary<TGroupKey, ICollection<TValue>>();
            _sortedValues = new DictionarySortedSet<TValue, TKey>(_comparer);
        }

        /// <summary>
        /// Gets comparer that preserves original comparer order, but also differentiates on <see cref="TValue"/> items based on their identity.
        /// </summary>
        /// <param name="comparer">Original comparer.</param>
        /// <returns>Identity comparer.</returns>
        protected abstract IComparer<TValue> GetUniqueComparer(IComparer<TValue> comparer);
        
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
            return _buckets.SelectMany(b => b.Value).ToArray();
        }
        
        /// <summary>
        /// Gets all items in groups in supplied comparer order in groups.
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public IDictionary<TGroupKey, TValue[]> GetBucketSnapshot()
        {
            return _buckets.ToDictionary(g => g.Key, g => g.Value.ToArray());
        }
        
        /// <summary>
        /// Gets first element in supplied comparer order.
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryTakeFirst(out TValue first) => TryRemove(_sortedValues.Min.Value, out first);

        /// <summary>
        /// Tries to remove element.
        /// </summary>
        /// <param name="key">Key to be removed.</param>
        /// <param name="value">Removed element or null.</param>
        /// <param name="bucket">Bucket for same sender transactions.</param>
        /// <returns>If element was removed. False if element was not present in pool.</returns>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryRemove(TKey key, out TValue value, out ICollection<TValue> bucket)
        {
            if (_cacheMap.TryGetValue(key, out value))
            {
                if (Remove(key, value))
                {
                    TGroupKey groupMapping = MapToGroup(value);
                    if (_buckets.TryGetValue(groupMapping, out bucket))
                    {
                        bucket.Remove(value);
                        return true;
                    }
                }

            }

            value = default;
            bucket = null;
            return false;
        }

        public bool TryRemove(TKey key, out TValue value) => TryRemove(key, out value, out _);
        public bool TryRemove(TKey key) => TryRemove(key, out _, out _);

        /// <summary>
        /// Tries to get element.
        /// </summary>
        /// <param name="key">Key to be returned.</param>
        /// <param name="value">Returned element or null.</param>
        /// <returns>If element retrieval succeeded. True if element was present in pool.</returns>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetValue(TKey key, out TValue value)
        {
            value = default;
            return _cacheMap.TryGetValue(key, out value);
        }

        /// <summary>
        /// Tries to insert element.
        /// </summary>
        /// <param name="key">Key to be inserted.</param>
        /// <param name="value">Element to insert.</param>
        /// <returns>If element was inserted. False if element was already present in pool.</returns>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryInsert(TKey key, TValue value)
        {
            if (CanInsert(key, value))
            {
                TGroupKey group = MapToGroup(value);

                if (!_buckets.TryGetValue(group, out ICollection<TValue> bucket))
                {
                    _buckets[group] = bucket = new SortedSet<TValue>(_comparer);
                }

                InsertCore(key, value, bucket);

                if (_cacheMap.Count > _capacity)
                {
                    RemoveLast();
                }

                return true;
            }

            return false;
        }

        private void RemoveLast()
        {
            TryRemove(_sortedValues.Max.Value);
        }
        
        /// <summary>
        /// Checks if element can be inserted.
        /// </summary>
        protected virtual bool CanInsert(TKey key, TValue value)
        {
            if (value == null)
            {
                throw new ArgumentNullException();
            }

            return !_cacheMap.ContainsKey(key);
        }
        
        /// <summary>
        /// Actual insert mechanism.
        /// </summary>
        protected virtual void InsertCore(TKey key, TValue value, ICollection<TValue> bucketCollection)
        {
            bucketCollection.Add(value);
            _cacheMap.Add(key, value);
            _sortedValues.Add(value, key);
        }
        
        /// <summary>
        /// Actual removal mechanism. 
        /// </summary>
        protected virtual bool Remove(TKey key, TValue value)
        {
            _sortedValues.Remove(value);
            return _cacheMap.Remove(key);
        }
    }
}
