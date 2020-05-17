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

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    /// <summary>
    /// https://stackoverflow.com/questions/754233/is-it-there-any-lru-implementation-of-idictionary
    /// </summary>
    public class LruCache<TKey, TValue> : ICache<TKey, TValue>
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly LinkedList<LruCacheItem> _lruList;

        public void Clear()
        {
            _cacheMap?.Clear();
            _lruList?.Clear();
        }

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            _maxCapacity = maxCapacity;
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>) Bytes.EqualityComparer)
                : new Dictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity); // do not initialize it at the full capacity
            _lruList = new LinkedList<LruCacheItem>();
        }

        public LruCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                TValue value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return value;
            }

            return default;
        }
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGet(TKey key, out TValue value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Set(TKey key, TValue val)
        {
            if (val == null)
            {
                Delete(key);
                return;
            }

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                node.Value.Value = val;
                _lruList.Remove(node);
                _lruList.AddLast(node);
            }
            else
            {
                if (_cacheMap.Count >= _maxCapacity)
                {
                    RemoveFirst();
                }

                LruCacheItem cacheItem = new LruCacheItem(key, val);
                LinkedListNode<LruCacheItem> newNode = new LinkedListNode<LruCacheItem>(cacheItem);
                _lruList.AddLast(newNode);
                _cacheMap.Add(key, newNode);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Delete(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                _lruList.Remove(node);
                _cacheMap.Remove(key);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Contains(TKey key) => _cacheMap.ContainsKey(key);

        private void RemoveFirst()
        {
            LinkedListNode<LruCacheItem> node = _lruList.First;
            _lruList.RemoveFirst();

            _cacheMap.Remove(node.Value.Key);
        }

        private class LruCacheItem
        {
            public LruCacheItem(TKey k, TValue v)
            {
                Key = k;
                Value = v;
            }

            public readonly TKey Key;
            public TValue Value;
        }

        public int MemorySize => CalculateMemorySize(0, _cacheMap.Count);

        public static int CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
        {
            // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)
            
            const int preInit = 48 /* LinkedList */ + 80 /* Dictionary */ + 24;
            int postInit = 52 /* lazy init of two internal dictionary arrays + dictionary size times (entry size + int) */ + MemorySizes.FindNextPrime(currentItemsCount) * 28 + currentItemsCount * 80 /* LinkedListNode and CacheItem times items count */;
            return MemorySizes.Align(preInit + postInit + keyPlusValueSize * currentItemsCount);
        }
    }
}