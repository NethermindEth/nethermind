//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    /// <remarks>
    /// The array based solution is preferred to lower the overall memory management overhead. The <see cref="LinkedListNode{T}"/> based approach is very costly.
    /// </remarks>
    /// <summary>
    /// https://stackoverflow.com/questions/754233/is-it-there-any-lru-implementation-of-idictionary
    /// </summary>
    public class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly LinkedList<LruCacheItem> _lruList;

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Clear()
        {
            _cacheMap?.Clear();
            _lruList?.Clear();
        }

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            if (maxCapacity < 1) 
            {
                throw new ArgumentOutOfRangeException();
            }

            _maxCapacity = maxCapacity;
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
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
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                TValue value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return value;
            }

#pragma warning disable 8603
            // fixed C# 9
            return default;
#pragma warning restore 8603
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGet(TKey key, out TValue value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return true;
            }

#pragma warning disable 8601
            // fixed C# 9
            value = default;
#pragma warning restore 8601
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Set(TKey key, TValue val)
        {
            if (val is null)
            {
                Delete(key);
                return;
            }

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                node.Value.Value = val;
                _lruList.Remove(node);
                _lruList.AddLast(node);
            }
            else
            {
                if (_cacheMap.Count >= _maxCapacity)
                {
                    Replace(key, val);
                }
                else
                {
                    LruCacheItem cacheItem = new LruCacheItem(key, val);
                    LinkedListNode<LruCacheItem> newNode = new LinkedListNode<LruCacheItem>(cacheItem);
                    _lruList.AddLast(newNode);
                    _cacheMap.Add(key, newNode);
                }
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Delete(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                _lruList.Remove(node);
                _cacheMap.Remove(key);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Contains(TKey key) => _cacheMap.ContainsKey(key);

        private void Replace(TKey key, TValue value)
        {
            LinkedListNode<LruCacheItem>? node = _lruList.First;
            _lruList.RemoveFirst();
            _cacheMap.Remove(node.Value.Key);

            node.Value.Value = value;
            node.Value.Key = key;
            _lruList.AddLast(node);
            _cacheMap.Add(key, node);
        }

        private class LruCacheItem
        {
            public LruCacheItem(TKey k, TValue v)
            {
                Key = k;
                Value = v;
            }

            public TKey Key;
            public TValue Value;
        }

        public long MemorySize => CalculateMemorySize(0, _cacheMap.Count);

        public static long CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
        {
            // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)

            const int preInit = 48 /* LinkedList */ + 80 /* Dictionary */ + 24;
            int postInit = 52 /* lazy init of two internal dictionary arrays + dictionary size times (entry size + int) */ + MemorySizes.FindNextPrime(currentItemsCount) * 28 + currentItemsCount * 80 /* LinkedListNode and CacheItem times items count */;
            return MemorySizes.Align(preInit + postInit + keyPlusValueSize * currentItemsCount);
        }
    }
}
