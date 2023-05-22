// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;

namespace Nethermind.Core.Caching
{
    /// <summary>
    /// Its like `LruCache` but you can index the key by span.
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    [DebuggerDisplay("SpanLruCache (Single: {SingleAccessCount}, Multi: {MultiAccessCount})")]
    public sealed class SpanLruCache<TKey, TValue> : ISpanCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly SpanDictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private LinkedListNode<LruCacheItem>? _singleAccessLru;
        private LinkedListNode<LruCacheItem>? _multiAccessLru;

        public int SingleAccessCount { get; private set; }
        public int MultiAccessCount { get; private set; }

        public SpanLruCache(int maxCapacity, int startCapacity, string name, ISpanEqualityComparer<TKey> comparer)
        {
            if (maxCapacity < 1)
            {
                throw new ArgumentOutOfRangeException();
            }

            _maxCapacity = maxCapacity;
            _cacheMap = new SpanDictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity, comparer);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Clear()
        {
            _singleAccessLru = null;
            _multiAccessLru = null;
            SingleAccessCount = 0;
            MultiAccessCount = 0;
            _cacheMap.Clear();
        }

        public TValue Get(ReadOnlySpan<TKey> key)
        {
            if (TryGet(key, out TValue value))
            {
                return value;
            }

#pragma warning disable 8603
            // fixed C# 9
            return default;
#pragma warning restore 8603
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGet(ReadOnlySpan<TKey> key, out TValue value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                ulong accessCount = node.AccessCount;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _singleAccessLru, ref _multiAccessLru, node);
                if (accessCount == 1)
                {
                    SingleAccessCount--;
                    MultiAccessCount++;
                }

                return true;
            }

#pragma warning disable 8601
            // fixed C# 9
            value = default;
#pragma warning restore 8601
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Set(ReadOnlySpan<TKey> key, TValue val)
        {
            if (val is null)
            {
                return Delete(key);
            }

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                node.Value.Value = val;
                ulong accessCount = node.AccessCount;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _singleAccessLru, ref _multiAccessLru, node);
                if (accessCount == 1)
                {
                    SingleAccessCount--;
                    MultiAccessCount++;
                }

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
                    SingleAccessCount++;
                    LinkedListNode<LruCacheItem> newNode = new(new(keyAsArray, val));
                    LinkedListNode<LruCacheItem>.AddMostRecent(ref _singleAccessLru, newNode);
                    _cacheMap.Add(keyAsArray, newNode);
                }

                return true;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Delete(ReadOnlySpan<TKey> key)
        {
            if (_cacheMap.Remove(key, out LinkedListNode<LruCacheItem>? node))
            {
                if (node.AccessCount == 1)
                {
                    SingleAccessCount--;
                    LinkedListNode<LruCacheItem>.Remove(ref _singleAccessLru, node);
                }
                else
                {
                    MultiAccessCount--;
                    LinkedListNode<LruCacheItem>.Remove(ref _multiAccessLru, node);
                }

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Contains(ReadOnlySpan<TKey> key) => _cacheMap.ContainsKey(key);

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IDictionary<TKey[], TValue> Clone() => _cacheMap.ToDictionary(i => i.Key, i => i.Value.Value.Value);

        [MethodImpl(MethodImplOptions.Synchronized)]
        public KeyValuePair<TKey[], TValue>[] ToArray() => _cacheMap.Select(kv => new KeyValuePair<TKey[], TValue>(kv.Key, kv.Value.Value.Value)).ToArray();

        private void Replace(ReadOnlySpan<TKey> key, TValue value)
        {
            LinkedListNode<LruCacheItem>? node;
            if (_singleAccessLru is null ||
                // Reserve 1/4 of the cache for multi access items.
                // Prefer the single access item to evict,
                // if multi-access item was accessed more recently.
                (MultiAccessCount > _maxCapacity / 4
                 && _multiAccessLru!.LastAccessSec < _singleAccessLru.LastAccessSec))
            {
                MultiAccessCount--;
                SingleAccessCount++;
                node = _multiAccessLru;
                Debug.Assert(node is not null && node.AccessCount > 1);
                LinkedListNode<LruCacheItem>.Remove(ref _multiAccessLru, node);
            }
            else
            {
                node = _singleAccessLru;
                Debug.Assert(node is not null && node.AccessCount == 1);
                LinkedListNode<LruCacheItem>.Remove(ref _singleAccessLru, node);
            }

            if (node is null)
            {
                ThrowInvalidOperationException();
            }

            if (!_cacheMap.Remove(node.Value.Key))
            {
                ThrowInvalidOperationException();
            }

            TKey[] keyAsArray = key.ToArray();
            node.Value = new(keyAsArray, value);
            node.ResetAccessCount();

            LinkedListNode<LruCacheItem>.AddMostRecent(ref _singleAccessLru, node);
            _cacheMap.Add(keyAsArray, node);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException(
                $"{nameof(SpanLruCache<TKey, TValue>)} called {nameof(Replace)} when empty.");
        }

        [DebuggerDisplay("{Key}:{Value}")]
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
