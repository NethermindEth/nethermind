//  Copyright (c) 2018 Demerzel Solutions Limited
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
    public class SortedPool<TKey, TValue, TGroup>
    {
        private readonly int _capacity;
        private readonly Func<TValue, TGroup> _groupMapping;
        private readonly IDictionary<TGroup, ICollection<TValue>> _buckets;
        private readonly DictionarySortedSet<TValue, TKey> _sortedValues;
        
        protected readonly IDictionary<TKey, TValue> CacheMap;
        protected readonly IComparer<TValue> Comparer;

        public SortedPool(int capacity, IComparer<TValue> comparer, Func<TValue, TGroup> groupMapping)
        {
            _capacity = capacity;
            Comparer = comparer;
            _groupMapping = groupMapping;
            CacheMap = new Dictionary<TKey, TValue>(); // do not initialize it at the full capacity
            _buckets = new Dictionary<TGroup, ICollection<TValue>>();
            _sortedValues = new DictionarySortedSet<TValue, TKey>(comparer);
        }

        public int Count => CacheMap.Count;

        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue[] GetSnapshot()
        {
            lock (CacheMap)
            {
                return _buckets.SelectMany(b => b.Value).ToArray();
            }
        }
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        public IDictionary<TGroup, TValue[]> GetBucketSnapshot()
        {
            lock (CacheMap)
            {
                return _buckets.ToDictionary(g => g.Key, g => g.Value.ToArray());
            }
        }
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue TakeFirst()
        {
            TryRemove(_sortedValues.Min.Value, out TValue value);
            return value;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryRemove(TKey key, out TValue value)
        {
            if (CacheMap.TryGetValue(key, out value))
            {
                lock (CacheMap)
                {
                    if (Remove(key, value))
                    {
                        TGroup groupMapping = _groupMapping(value);
                        if (_buckets.TryGetValue(groupMapping, out var collection))
                        {
                            collection.Remove(value);
                            return true;
                        }
                    }
                }
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetValue(TKey key, out TValue value)
        {
            value = default;
            return CacheMap.TryGetValue(key, out value);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryInsert(TKey key, TValue value)
        {
            if (CanInsert(key, value))
            {
                TGroup group = _groupMapping(value);

                lock (CacheMap)
                {
                    if (!_buckets.TryGetValue(group, out ICollection<TValue> bucket))
                    {
                        _buckets[group] = bucket = new SortedSet<TValue>(Comparer);
                    }

                    InsertCore(key, value, bucket);
                }

                if (CacheMap.Count > _capacity)
                {
                    RemoveLast();
                }

                return true;
            }

            return false;
        }

        private void RemoveLast()
        {
            TryRemove(_sortedValues.Max.Value, out _);
        }
        
        protected virtual bool CanInsert(TKey key, TValue value)
        {
            if (value == null)
            {
                throw new ArgumentNullException();
            }

            return !CacheMap.TryGetValue(key, out _);
        }
        
        protected virtual void InsertCore(TKey key, TValue value, ICollection<TValue> bucket)
        {
            bucket.Add(value);
            CacheMap.Add(key, value);
            _sortedValues.Add(value, key);
        }
        
        protected virtual bool Remove(TKey key, TValue value)
        {
            _sortedValues.Remove(value);
            return CacheMap.Remove(key);
        }
    }
}
