// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching
{
    public sealed class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly McsLock _lock = new();
        private readonly string _name;
        private readonly Action<TValue>? _onEvict;
        private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;

        public LruCache(int maxCapacity, int startCapacity, string name, Action<TValue>? onEvict = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _name = name;
            _maxCapacity = maxCapacity;
            _onEvict = onEvict;
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new Dictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity); // do not initialize it at the full capacity
        }

        public LruCache(int maxCapacity, string name, Action<TValue>? onEvict = null)
            : this(maxCapacity, 0, name, onEvict)
        {
        }

        public void Clear()
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            NotifyEvictedValues();
            _leastRecentlyUsed = null;
            _cacheMap.Clear();
        }

        public TValue Get(TKey key)
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                TValue value = node.Value.Value;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return value;
            }

            return default!;
        }

        public bool TryGet(TKey key, out TValue value)
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Sets a missing cached value or atomically returns the existing one for the specified key.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="state">State passed to <paramref name="valueFactory"/> without requiring a closure.</param>
        /// <param name="valueFactory">Factory used to create the value when the key is missing.</param>
        /// <typeparam name="TState">Type of the factory state.</typeparam>
        /// <returns>The existing value, or the value created by <paramref name="valueFactory"/>.</returns>
        public TValue SetOrGet<TState>(TKey key, TState state, Func<TKey, TState, TValue> valueFactory)
        {
            ArgumentNullException.ThrowIfNull(valueFactory);

            using McsLock.Disposable lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                TValue value = node.Value.Value;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return value;
            }

            TValue newValue = valueFactory(key, state);
            if (newValue is null)
            {
                return newValue;
            }

            if (_cacheMap.Count >= _maxCapacity)
            {
                Replace(key, newValue);
            }
            else
            {
                LinkedListNode<LruCacheItem> newNode = new(new(key, newValue));
                LinkedListNode<LruCacheItem>.AddMostRecent(ref _leastRecentlyUsed, newNode);
                _cacheMap.Add(key, newNode);
            }

            return newValue;
        }

        public bool Set(TKey key, TValue val)
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            if (val is null)
            {
                return DeleteNoLock(key);
            }

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                NotifyEvicted(node.Value.Value);
                node.Value.Value = val;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return false;
            }

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

        public bool Delete(TKey key)
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            return DeleteNoLock(key);
        }

        /// <summary>
        /// Deletes a cached value and returns it when the key is present.
        /// </summary>
        public bool TryRemove(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                RemoveNoLock(key, node, notifyEviction: false);
                return true;
            }

            value = default;
            return false;
        }

        private bool DeleteNoLock(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                RemoveNoLock(key, node, notifyEviction: true);
                return true;
            }

            return false;
        }

        private void RemoveNoLock(TKey key, LinkedListNode<LruCacheItem> node, bool notifyEviction)
        {
            if (notifyEviction)
            {
                NotifyEvicted(node.Value.Value);
            }

            LinkedListNode<LruCacheItem>.Remove(ref _leastRecentlyUsed, node);
            _cacheMap.Remove(key);
        }

        public bool Contains(TKey key)
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            return _cacheMap.ContainsKey(key);
        }

        public KeyValuePair<TKey, TValue>[] ToArray()
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            int i = 0;
            KeyValuePair<TKey, TValue>[] array = new KeyValuePair<TKey, TValue>[_cacheMap.Count];
            foreach (KeyValuePair<TKey, LinkedListNode<LruCacheItem>> kvp in _cacheMap)
            {
                array[i++] = new KeyValuePair<TKey, TValue>(kvp.Key, kvp.Value.Value.Value);
            }

            return array;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue[] GetValues()
        {
            int i = 0;
            TValue[] array = new TValue[_cacheMap.Count];
            foreach (KeyValuePair<TKey, LinkedListNode<LruCacheItem>> kvp in _cacheMap)
            {
                array[i++] = kvp.Value.Value.Value;
            }

            return array;
        }

        public int Count => _cacheMap.Count;

        private void Replace(TKey key, TValue value)
        {
            LinkedListNode<LruCacheItem>? node = _leastRecentlyUsed;
            if (node is null)
            {
                ThrowInvalidOperationException();
            }

            TValue evictedValue = node!.Value.Value;
            _cacheMap.Remove(node.Value.Key);

            node.Value = new(key, value);
            LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
            _cacheMap.Add(key, node);
            NotifyEvicted(evictedValue);

            [DoesNotReturn]
            static void ThrowInvalidOperationException() => throw new InvalidOperationException(
                    $"{nameof(LruCache<TKey, TValue>)} called {nameof(Replace)} when empty.");
        }

        private void NotifyEvictedValues()
        {
            if (_onEvict is null)
            {
                return;
            }

            foreach (KeyValuePair<TKey, LinkedListNode<LruCacheItem>> kvp in _cacheMap)
            {
                _onEvict(kvp.Value.Value.Value);
            }
        }

        private void NotifyEvicted(TValue value)
        {
            if (_onEvict is not null && value is not null)
            {
                _onEvict(value);
            }
        }

        private struct LruCacheItem(TKey k, TValue v)
        {
            public readonly TKey Key = k;
            public TValue Value = v;
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
