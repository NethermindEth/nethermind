// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    [DebuggerDisplay("LruCache (Single: {SingleAccessCount}, Multi: {MultiAccessCount})")]
    public sealed class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private LinkedListNode<LruCacheItem>? _singleAccessLru;
        private LinkedListNode<LruCacheItem>? _multiAccessLru;

        public int SingleAccessCount { get; private set; }
        public int MultiAccessCount { get; private set; }

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            if (maxCapacity < 1)
            {
                throw new ArgumentOutOfRangeException();
            }

            _maxCapacity = maxCapacity;
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new Dictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity); // do not initialize it at the full capacity
        }

        public LruCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
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

        public TValue Get(TKey key)
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
        public bool TryGet(TKey key, out TValue value)
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
        public bool Set(TKey key, TValue val)
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
                    SingleAccessCount++;
                    LinkedListNode<LruCacheItem> newNode = new(new(key, val));
                    LinkedListNode<LruCacheItem>.AddMostRecent(ref _singleAccessLru, newNode);
                    _cacheMap.Add(key, newNode);
                }

                return true;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Delete(TKey key)
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
        public bool Contains(TKey key) => _cacheMap.ContainsKey(key);

        [MethodImpl(MethodImplOptions.Synchronized)]
        public KeyValuePair<TKey, TValue>[] ToArray()
        {
            int i = 0;
            KeyValuePair<TKey, TValue>[] array = new KeyValuePair<TKey, TValue>[_cacheMap.Count];
            foreach (KeyValuePair<TKey, LinkedListNode<LruCacheItem>> kvp in _cacheMap)
            {
                array[i++] = new KeyValuePair<TKey, TValue>(kvp.Key, kvp.Value.Value.Value);
            }

            return array;
        }

        private void Replace(TKey key, TValue value)
        {
            LinkedListNode<LruCacheItem>? node;
            if (_singleAccessLru is null ||
                (MultiAccessCount > _maxCapacity / 2
                 // Only if last access was earlier than the oldest single access item
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

            node.Value = new(key, value);
            node.ResetAccessCount();

            LinkedListNode<LruCacheItem>.AddMostRecent(ref _singleAccessLru, node);
            _cacheMap.Add(key, node);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException(
                $"{nameof(LruCache<TKey, TValue>)} called {nameof(Replace)} when empty.");
        }

        [DebuggerDisplay("{Key}:{Value}")]
        private struct LruCacheItem
        {
            public LruCacheItem(TKey k, TValue v)
            {
                Key = k;
                Value = v;
            }

            public TKey Key;
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
