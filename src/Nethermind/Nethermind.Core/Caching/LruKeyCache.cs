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
    /// <summary>
    /// https://stackoverflow.com/questions/754233/is-it-there-any-lru-implementation-of-idictionary
    /// </summary>
    public class LruKeyCache<TKey> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly string _name;
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _cacheMap;
        private readonly LinkedList<TKey> _lruList;

        public void Clear()
        {
            _cacheMap.Clear();
            _lruList.Clear();
        }

        public LruKeyCache(int maxCapacity, int startCapacity, string name)
        {
            _maxCapacity = maxCapacity;
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<TKey>>((IEqualityComparer<TKey>) Bytes.EqualityComparer)
                : new Dictionary<TKey, LinkedListNode<TKey>>(startCapacity); // do not initialize it at the full capacity
            _lruList = new LinkedList<TKey>();
        }

        public LruKeyCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Set(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                _lruList.Remove(node);
                _lruList.AddLast(node);
            }
            else
            {
                if (_cacheMap.Count >= _maxCapacity)
                {
                    Replace(key);
                }
                else
                {
                    LinkedListNode<TKey> newNode = new(key);
                    _lruList.AddLast(newNode);
                    _cacheMap.Add(key, newNode);    
                }
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Delete(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                _lruList.Remove(node);
                _cacheMap.Remove(key);
            }
        }

        private void Replace(TKey key)
        {
            // TODO: some potential null ref issue here?
            
            LinkedListNode<TKey>? node = _lruList.First;
            if (node is null)
            {
                throw new InvalidOperationException(
                    $"{nameof(LruKeyCache<TKey>)} called {nameof(Replace)} when empty.");
            }
            
            _lruList.RemoveFirst();
            _cacheMap.Remove(node.Value);

            node.Value = key;
            _lruList.AddLast(node);
            _cacheMap.Add(node.Value, node);
        }

        public long MemorySize => CalculateMemorySize(0, _cacheMap.Count);

        // TODO: memory size on the KeyCache will be smaller because we do not create LruCacheItems
        public static long CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
        {
            // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)
            
            const int preInit = 48 /* LinkedList */ + 80 /* Dictionary */ + 24;
            int postInit = 52 /* lazy init of two internal dictionary arrays + dictionary size times (entry size + int) */ + MemorySizes.FindNextPrime(currentItemsCount) * 28 + currentItemsCount * 80 /* LinkedListNode and CacheItem times items count */;
            return MemorySizes.Align(preInit + postInit + keyPlusValueSize * currentItemsCount);
        }
    }
}
