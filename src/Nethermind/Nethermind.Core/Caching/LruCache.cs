// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    public sealed class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _maxCapacity = maxCapacity;
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new Dictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity); // do not initialize it at the full capacity
        }

        public LruCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        public void Clear()
        {
            _leastRecentlyUsed = null;
            _cacheMap.Clear();
        }

        public TValue Get(TKey key) => TryGet(key, out TValue value) ? value : default!;

        public bool TryGet(TKey key, out TValue value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return true;
            }

            value = default!;
            return false;
        }

        public bool Set(TKey key, TValue val)
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
                if (_cacheMap.Count >= _maxCapacity)
                {
                    Replace(key, val);
                }
                else
                {
                    LinkedListNode<LruCacheItem> newNode = new(new(key, val));
                    LinkedListNode<LruCacheItem>.AddMostRecent(ref _leastRecentlyUsed, newNode);
                    _cacheMap.Add(key, newNode);
                }

                return true;
            }
        }

        public bool Delete(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                LinkedListNode<LruCacheItem>.Remove(ref _leastRecentlyUsed, node);
                _cacheMap.Remove(key);
                return true;
            }

            return false;
        }

        public bool Contains(TKey key)
        {
            return _cacheMap.ContainsKey(key);
        }

        private void Replace(TKey key, TValue value)
        {
            LinkedListNode<LruCacheItem>? node = _leastRecentlyUsed;
            if (node is null)
            {
                ThrowInvalidOperationException();
            }

            _cacheMap.Remove(node!.Value.Key);

            node.Value = new(key, value);
            LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
            _cacheMap.Add(key, node);

            [DoesNotReturn]
            static void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException(
                    $"{nameof(LruCache<TKey, TValue>)} called {nameof(Replace)} when empty.");
            }
        }

        private struct LruCacheItem
        {
            public LruCacheItem(TKey k, TValue v)
            {
                Key = k;
                Value = v;
            }

            public readonly TKey Key;
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
