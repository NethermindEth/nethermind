/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    /// <summary>
    /// https://stackoverflow.com/questions/754233/is-it-there-any-lru-implementation-of-idictionary
    /// </summary>
    public class LruCache<TKey, TValue> : ICache<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly LinkedList<LruCacheItem> _lruList;

        public LruCache(int capacity)
        {
            _capacity = capacity;
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new Dictionary<TKey, LinkedListNode<LruCacheItem>>(); // do not initialize it at the full capacity
            _lruList = new LinkedList<LruCacheItem>();
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

            return default(TValue);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Set(TKey key, TValue val)
        {
            if (val == null)
            {
                Delete(key);
            }

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                node.Value.Value = val;
                _lruList.Remove(node);
                _lruList.AddLast(node);
            }
            else
            {
                if (_cacheMap.Count >= _capacity)
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
    }
}