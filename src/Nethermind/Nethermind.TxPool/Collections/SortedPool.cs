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

namespace Nethermind.TxPool.Collections
{
    public class SortedPool<TKey, TValue, TGroup>
    {
        private readonly int _capacity;
        protected readonly Comparison<TValue> Comparison;
        protected readonly Func<TValue, TGroup> GroupMapping;
        protected readonly IDictionary<TKey, TValue> CacheMap;
        protected readonly IDictionary<TGroup, ICollection<TValue>> Buckets;

        public SortedPool(int capacity, Comparison<TValue> comparison, Func<TValue, TGroup> groupMapping)
        {
            _capacity = capacity;
            Comparison = comparison;
            GroupMapping = groupMapping;
            CacheMap = new Dictionary<TKey, TValue>(); // do not initialize it at the full capacity
            Buckets = new Dictionary<TGroup, ICollection<TValue>>();
        }

        public int Count => CacheMap.Count;

        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue[] GetSnapshot()
        {
            return Buckets.SelectMany(b => b.Value).ToArray();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryRemove(TKey key, out TValue tx)
        {
            if (CacheMap.TryGetValue(key, out tx))
            {
                if (Remove(key))
                {
                    TGroup groupMapping = GroupMapping(tx);
                    if (Buckets.TryGetValue(groupMapping, out var collection))
                    {
                        collection.Remove(tx);
                        return true;
                    }
                }
            }

            tx = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetValue(TKey key, out TValue tx)
        {
            tx = default;
            return CacheMap.TryGetValue(key, out tx);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryInsert(TKey key, TValue val)
        {
            if (CanInsert(key, val))
            {
                KeyValuePair<TKey, TValue> cacheItem = new KeyValuePair<TKey, TValue>(key, val);
                LinkedListNode<KeyValuePair<TKey, TValue>> newNode = new LinkedListNode<KeyValuePair<TKey, TValue>>(cacheItem);


                LinkedListNode<KeyValuePair<TKey, TValue>> node = LruList.First;
                bool added = false;
                while (node != null)
                {
                    if (Comparison(node.Value.Value, val) < 0)
                    {
                        LruList.AddBefore(node, newNode);
                        added = true;
                        break;
                    }

                    node = node.Next;
                }

                if (!added)
                {
                    LruList.AddLast(newNode);
                }

                InsertCore(key, newNode);

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
            LinkedListNode<KeyValuePair<TKey, TValue>> node = LruList.Last;
            LruList.RemoveLast();

            Remove(node.Value.Key);
        }
        
        protected virtual bool CanInsert(TKey key, TValue value)
        {
            if (value == null)
            {
                throw new ArgumentNullException();
            }

            if (CacheMap.TryGetValue(key, out _))
            {
                return false;
            }

            return true;
        }
        
        protected virtual void InsertCore(TKey key, LinkedListNode<KeyValuePair<TKey, TValue>> newNode)
        {
            CacheMap.Add(key, newNode);
        }
        
        protected virtual bool Remove(TKey key) => CacheMap.Remove(key);
    }
}
