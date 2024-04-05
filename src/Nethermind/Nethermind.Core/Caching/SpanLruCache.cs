// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching
{
    /// <summary>
    /// Its like `LruCache` but you can index the key by span.
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    public sealed class SpanLruCache<TKey, TValue> : ISpanCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly SpanDictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly McsLock _lock = new();
        private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;

        public SpanLruCache(int maxCapacity, int startCapacity, string name, ISpanEqualityComparer<TKey> comparer)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _maxCapacity = maxCapacity;
            _cacheMap = new SpanDictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity, comparer);
        }

        public void Clear()
        {
            using var lockRelease = _lock.Acquire();

            _leastRecentlyUsed = null;
            _cacheMap.Clear();
        }

        public TValue Get(ReadOnlySpan<TKey> key)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                TValue value = node.Value.Value;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return value;
            }

#pragma warning disable 8603
            // fixed C# 9
            return default;
#pragma warning restore 8603
        }

        public bool TryGet(ReadOnlySpan<TKey> key, out TValue value)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return true;
            }

#pragma warning disable 8601
            // fixed C# 9
            value = default;
#pragma warning restore 8601
            return false;
        }

        public bool Set(ReadOnlySpan<TKey> key, TValue val)
        {
            using var lockRelease = _lock.Acquire();

            if (val is null)
            {
                return DeleteNoLock(key);
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
                    TKey[] keyAsArray = key.ToArray();
                    LinkedListNode<LruCacheItem> newNode = new(new(keyAsArray, val));
                    LinkedListNode<LruCacheItem>.AddMostRecent(ref _leastRecentlyUsed, newNode);
                    _cacheMap.Add(keyAsArray, newNode);
                }

                return true;
            }
        }

        public bool Delete(ReadOnlySpan<TKey> key)
        {
            using var lockRelease = _lock.Acquire();

            return DeleteNoLock(key);
        }

        private bool DeleteNoLock(ReadOnlySpan<TKey> key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                LinkedListNode<LruCacheItem>.Remove(ref _leastRecentlyUsed, node);
                _cacheMap.Remove(key);
                return true;
            }

            return false;
        }

        public bool Contains(ReadOnlySpan<TKey> key)
        {
            using var lockRelease = _lock.Acquire();

            return _cacheMap.ContainsKey(key);
        }

        public IDictionary<TKey[], TValue> Clone()
        {
            using var lockRelease = _lock.Acquire();

            return _cacheMap.ToDictionary(i => i.Key, i => i.Value.Value.Value);
        }

        public KeyValuePair<TKey[], TValue>[] ToArray()
        {
            using var lockRelease = _lock.Acquire();

            return _cacheMap.Select(kv => new KeyValuePair<TKey[], TValue>(kv.Key, kv.Value.Value.Value)).ToArray();
        }

        private void Replace(ReadOnlySpan<TKey> key, TValue value)
        {
            LinkedListNode<LruCacheItem>? node = _leastRecentlyUsed;
            if (node is null)
            {
                ThrowInvalidOperationException();
            }

            _cacheMap.Remove(node!.Value.Key);

            TKey[] keyAsArray = key.ToArray();
            node.Value = new(keyAsArray, value);
            LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
            _cacheMap.Add(keyAsArray, node);

            [DoesNotReturn]
            static void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException(
                    $"{nameof(LruCache<TKey, TValue>)} called {nameof(Replace)} when empty.");
            }
        }

        private struct LruCacheItem
        {
            public LruCacheItem(TKey[] k, TValue v)
            {
                Key = k;
                Value = v;
            }

            public readonly TKey[] Key;
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
