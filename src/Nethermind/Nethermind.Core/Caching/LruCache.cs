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

using System.Collections.Generic;
using BitFaster.Caching.Lru;

namespace Nethermind.Core.Caching
{
    public class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
    {
        private ConcurrentLru<TKey, TValue> _internalCache;
        private readonly int _maxCapacity;
        public void Clear() => _internalCache = new(_maxCapacity);

        public LruCache(int maxCapacity)
        {
            _maxCapacity = maxCapacity;
            _internalCache = new(maxCapacity);
        }

        public TValue Get(TKey key)
        {
            _ = _internalCache.TryGet(key, out TValue node);
            return node;
        }

        public bool TryGet(TKey key, out TValue value) => _internalCache.TryGet(key, out value);

        public void Set(TKey key, TValue val) => _internalCache.AddOrUpdate(key, val);

        public void Delete(TKey key) => _internalCache.TryRemove(key);

        public bool Contains(TKey key) => _internalCache.TryGet(key, out _);// _cacheMap.ContainsKey(key);

        public long MemorySize => CalculateMemorySize(0, _internalCache.Count);

        public static long CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
        {
            // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)

            const int preInit = 48 /* LinkedList */ + 80 /* Dictionary */ + 24;
            int postInit = 52 /* lazy init of two internal dictionary arrays + dictionary size times (entry size + int) */ + MemorySizes.FindNextPrime(currentItemsCount) * 28 + currentItemsCount * 80 /* LinkedListNode and CacheItem times items count */;
            return MemorySizes.Align(preInit + postInit + keyPlusValueSize * currentItemsCount);
        }
    }
}
