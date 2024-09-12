// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Caching
{
    public sealed class MemCountingCache : ICache<ValueHash256, byte[]>
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<ValueHash256, LinkedListNode<LruCacheItem>> _cacheMap;
        private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;

        private const int PreInitMemorySize =
            80 /* Dictionary */ +
            MemorySizes.SmallObjectOverhead +
            MemorySizes.SmallObjectOverhead +
            8 /* sizeof(int) aligned */;

        private const int PostInitMemorySize =
            52 /* lazy loaded dictionary.Items */ + PreInitMemorySize;

        private const int DictionaryItemSize = 28;
        private int _currentDictionaryCapacity;

        public void Clear()
        {
            _leastRecentlyUsed = null;
            _cacheMap?.Clear();
        }

        public MemCountingCache(int maxCapacity, int startCapacity, string name)
        {
            _maxCapacity = maxCapacity;
            _cacheMap = new(startCapacity); // do not initialize it at the full capacity
        }

        public MemCountingCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public byte[]? Get(ValueHash256 key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                byte[] value = node.Value.Value;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return value;
            }

            return default;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGet(ValueHash256 key, out byte[]? value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Set(ValueHash256 key, byte[]? val)
        {
            if (val is null)
            {
                return Delete(key);
            }

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                node.Value.Value = val;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return false;
            }
            else
            {
                long cacheItemMemory = LruCacheItem.FindMemorySize(val);
                int newCount = _cacheMap.Count + 1;
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
                    LinkedListNode<LruCacheItem> newNode = new(new(key, val));
                    LinkedListNode<LruCacheItem>.AddMostRecent(ref _leastRecentlyUsed, newNode);
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
        public bool Delete(ValueHash256 key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                MemorySize -= node.Value.MemorySize;
                LinkedListNode<LruCacheItem>.Remove(ref _leastRecentlyUsed, node);
                _cacheMap.Remove(key);

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Contains(ValueHash256 key) => _cacheMap.ContainsKey(key);

        private void Replace(ValueHash256 key, byte[] value)
        {
            LinkedListNode<LruCacheItem> node = _leastRecentlyUsed!;
            if (node is null)
            {
                ThrowInvalidOperation();
            }

            // ReSharper disable once PossibleNullReferenceException
            MemorySize += MemorySizes.Align(value.Length) - MemorySizes.Align(node.Value.Value.Length);

            _cacheMap.Remove(node!.Value.Key);

            node.Value = new(key, value);
            LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
            _cacheMap.Add(key, node);

            [DoesNotReturn]
            static void ThrowInvalidOperation()
            {
                throw new InvalidOperationException(
                                    $"{nameof(MemCountingCache)} called {nameof(Replace)} when empty.");
            }
        }

        private struct LruCacheItem
        {
            public LruCacheItem(ValueHash256 k, byte[] v)
            {
                Key = k;
                Value = v;
            }

            public readonly ValueHash256 Key;
            public byte[] Value;

            public readonly long MemorySize => FindMemorySize(Value);

            public static long FindMemorySize(byte[] withValue)
            {
                return MemorySizes.Align(
                    Hash256.MemorySize +
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
        public int Count => _cacheMap.Count;
    }
}
