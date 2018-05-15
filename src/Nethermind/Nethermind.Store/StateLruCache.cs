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
using Nethermind.Core;

namespace Nethermind.Store
{
    /// <summary>
    /// https://stackoverflow.com/questions/754233/is-it-there-any-lru-implementation-of-idictionary
    /// </summary>
    internal class StateLruCache
    {
        private readonly int _capacity;
        private readonly Dictionary<Address, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly LinkedList<LruCacheItem> _lruList;

        public StateLruCache(int capacity)
        {
            _capacity = capacity;
            _cacheMap = new Dictionary<Address, LinkedListNode<LruCacheItem>>(_capacity);
            _lruList = new LinkedList<LruCacheItem>();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public Account Get(Address key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                Account value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return value;
            }

            return default(Account);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Set(Address key, Account val)
        {
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

        private void RemoveFirst()
        {
            LinkedListNode<LruCacheItem> node = _lruList.First;
            _lruList.RemoveFirst();

            _cacheMap.Remove(node.Value.Key);
        }

        private class LruCacheItem
        {
            public LruCacheItem(Address k, Account v)
            {
                Key = k;
                Value = v;
            }

            public readonly Address Key;
            public Account Value;
        }
    }
}