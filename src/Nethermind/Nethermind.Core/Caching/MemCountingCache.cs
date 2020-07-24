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
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    /// <summary>
    /// https://stackoverflow.com/questions/754233/is-it-there-any-lru-implementation-of-idictionary
    /// </summary>
    public class MemCountingCache : ICache<Keccak, byte[]>
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<Keccak, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly LinkedList<LruCacheItem> _lruList;

        private const int PreInitMemorySize =
            48 /* LinkedList */ +
            80 /* Dictionary */ +
            MemorySizes.SmallObjectOverhead +
            8 /* sizeof(int) aligned */;

        private const int PostInitMemorySize =
            52 /* lazy loaded dictionary.Items */ + PreInitMemorySize;

        private const int DictionaryItemSize = 28;
        private int _currentDictionaryCapacity;

        public void Clear()
        {
            _cacheMap?.Clear();
            _lruList?.Clear();
        }

        public MemCountingCache(int maxCapacity, int startCapacity, string name)
        {
            _maxCapacity = maxCapacity;
            _cacheMap = typeof(Keccak) == typeof(byte[])
                ? new Dictionary<Keccak, LinkedListNode<LruCacheItem>>((IEqualityComparer<Keccak>) Bytes.EqualityComparer)
                : new Dictionary<Keccak, LinkedListNode<LruCacheItem>>(startCapacity); // do not initialize it at the full capacity
            _lruList = new LinkedList<LruCacheItem>();
        }

        public MemCountingCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public byte[] Get(Keccak key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                byte[] value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return value;
            }

            return default;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGet(Keccak key, out byte[] value)
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
        public void Set(Keccak key, byte[] val)
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
                int cacheItemMemory = LruCacheItem.FindMemorySize(val);
                int newCount = _lruList.Count + 1;
                int capacityRemembered = _currentDictionaryCapacity;
                int dictionaryNewMemory = CalculateDictionaryPartMemory(_currentDictionaryCapacity, newCount);
                int initialGrowth = newCount == 1 ? PostInitMemorySize - PreInitMemorySize : 0;
                int newMemorySize =
                    MemorySizes.Align(
                        MemorySize +
                        initialGrowth +
                        dictionaryNewMemory +
                        cacheItemMemory
                    );

                if (newMemorySize <= _maxCapacity)
                {
                    MemorySize = newMemorySize;
                    LruCacheItem cacheItem = new LruCacheItem(key, val);
                    LinkedListNode<LruCacheItem> newNode = new LinkedListNode<LruCacheItem>(cacheItem);
                    _lruList.AddLast(newNode);
                    _cacheMap.Add(key, newNode);
                }
                else
                {
                    _currentDictionaryCapacity = capacityRemembered;
                    Replace(key, val);
                }
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Delete(Keccak key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem> node))
            {
                MemorySize -= node.Value.MemorySize;
                _lruList.Remove(node);
                _cacheMap.Remove(key);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Contains(Keccak key) => _cacheMap.ContainsKey(key);

        private void Replace(Keccak key, byte[] value)
        {
            LinkedListNode<LruCacheItem> node = _lruList.First;
            
            // ReSharper disable once PossibleNullReferenceException
            MemorySize += MemorySizes.Align(value.Length) - MemorySizes.Align(node.Value.Value.Length);
            
            _lruList.RemoveFirst();
            _cacheMap.Remove(node.Value.Key);

            node.Value.Value = value;
            node.Value.Key = key;
            _lruList.AddLast(node);
            _cacheMap.Add(key, node);
        }

        private class LruCacheItem
        {
            public LruCacheItem(Keccak k, byte[] v)
            {
                Key = k;
                Value = v;
            }

            public Keccak Key;
            public byte[] Value;

            public int MemorySize => FindMemorySize(Value);

            public static int FindMemorySize(byte[] withValue)
            {
                return MemorySizes.Align(
                    Keccak.MemorySize +
                    MemorySizes.ArrayOverhead +
                    withValue.Length);
            }
        }

        private int CalculateDictionaryPartMemory(int currentCapacity, int newCount)
        {
            int previousSize = _currentDictionaryCapacity * DictionaryItemSize;
            int newSize = previousSize;
            if (newCount > currentCapacity)
            {
                _currentDictionaryCapacity = MemorySizes.FindNextPrime(Math.Max(currentCapacity, 1) * 2);
                newSize = _currentDictionaryCapacity * DictionaryItemSize;
            }

            return newSize - previousSize;
        }

        public int MemorySize { get; private set; } = PreInitMemorySize;

        public static int CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
        {
            // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)

            int postInit =
                52 /* lazy init of two internal dictionary arrays + dictionary size times (entry size + int) */
                + MemorySizes.FindNextPrime(currentItemsCount) * 28
                + currentItemsCount * 80 /* LinkedListNode and CacheItem times items count */;

            return MemorySizes.Align(
                PreInitMemorySize +
                postInit +
                keyPlusValueSize * currentItemsCount);
        }
    }
}