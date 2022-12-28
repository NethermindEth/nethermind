// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                ? new Dictionary<Keccak, LinkedListNode<LruCacheItem>>((IEqualityComparer<Keccak>)Bytes.EqualityComparer)
                : new Dictionary<Keccak, LinkedListNode<LruCacheItem>>(startCapacity); // do not initialize it at the full capacity
            _lruList = new LinkedList<LruCacheItem>();
        }

        public MemCountingCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public byte[]? Get(Keccak key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                byte[] value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return value;
            }

            return default;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGet(Keccak key, out byte[]? value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
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
        public bool Set(Keccak key, byte[]? val)
        {
            if (val is null)
            {
                return Delete(key);
            }

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                node.Value.Value = val;
                _lruList.Remove(node);
                _lruList.AddLast(node);
                return false;
            }
            else
            {
                long cacheItemMemory = LruCacheItem.FindMemorySize(val);
                int newCount = _lruList.Count + 1;
                int capacityRemembered = _currentDictionaryCapacity;
                long dictionaryNewMemory = CalculateDictionaryPartMemory(_currentDictionaryCapacity, newCount);
                int initialGrowth = newCount == 1 ? PostInitMemorySize - PreInitMemorySize : 0;
                long newMemorySize =
                    MemorySizes.Align(
                        MemorySize +
                        initialGrowth +
                        dictionaryNewMemory +
                        cacheItemMemory
                    );

                if (newMemorySize <= _maxCapacity)
                {
                    MemorySize = newMemorySize;
                    LruCacheItem cacheItem = new(key, val);
                    LinkedListNode<LruCacheItem> newNode = new(cacheItem);
                    _lruList.AddLast(newNode);
                    _cacheMap.Add(key, newNode);
                }
                else
                {
                    _currentDictionaryCapacity = capacityRemembered;
                    Replace(key, val);
                }

                return true;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Delete(Keccak key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                MemorySize -= node.Value.MemorySize;
                _lruList.Remove(node);
                _cacheMap.Remove(key);

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Contains(Keccak key) => _cacheMap.ContainsKey(key);

        private void Replace(Keccak key, byte[] value)
        {
            LinkedListNode<LruCacheItem>? node = _lruList.First;

            // ReSharper disable once PossibleNullReferenceException
            MemorySize += MemorySizes.Align(value.Length) - MemorySizes.Align(node?.Value.Value.Length ?? 0);

            _lruList.RemoveFirst();
            _cacheMap.Remove(node!.Value.Key);

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

            public long MemorySize => FindMemorySize(Value);

            public static long FindMemorySize(byte[] withValue)
            {
                return MemorySizes.Align(
                    Keccak.MemorySize +
                    MemorySizes.ArrayOverhead +
                    withValue.Length);
            }
        }

        private long CalculateDictionaryPartMemory(int currentCapacity, int newCount)
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

        public long MemorySize { get; private set; } = PreInitMemorySize;
    }
}
