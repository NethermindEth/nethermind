// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching
{
    public class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly McsLock _lock = new();
        private readonly string _name;
        private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _name = name;
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
            TValue[]? evictedValues = null;
            using (McsLock.Disposable lockRelease = _lock.Acquire())
            {
                if (_cacheMap.Count != 0)
                {
                    int i = 0;
                    evictedValues = new TValue[_cacheMap.Count];
                    foreach (KeyValuePair<TKey, LinkedListNode<LruCacheItem>> kvp in _cacheMap)
                    {
                        evictedValues[i++] = kvp.Value.Value.Value;
                    }
                }

                _leastRecentlyUsed = null;
                _cacheMap.Clear();
            }

            NotifyEvictedValues(evictedValues);
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

            TValue evictedValue = default!;
            bool notifyEviction = false;
            TValue result;
            using (McsLock.Disposable lockRelease = _lock.Acquire())
            {
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
                    evictedValue = Replace(key, newValue);
                    notifyEviction = true;
                }
                else
                {
                    LinkedListNode<LruCacheItem> newNode = new(new(key, newValue));
                    LinkedListNode<LruCacheItem>.AddMostRecent(ref _leastRecentlyUsed, newNode);
                    _cacheMap.Add(key, newNode);
                }

                result = newValue;
            }

            if (notifyEviction)
            {
                NotifyEvicted(evictedValue);
            }

            return result;
        }

        public bool Set(TKey key, TValue val)
        {
            TValue evictedValue = default!;
            bool notifyEviction = false;
            bool added;
            using (McsLock.Disposable lockRelease = _lock.Acquire())
            {
                if (val is null)
                {
                    added = DeleteNoLock(key, out evictedValue);
                    notifyEviction = added;
                }
                else if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
                {
                    evictedValue = node.Value.Value;
                    notifyEviction = true;
                    node.Value.Value = val;
                    LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                    added = false;
                }
                else if (_cacheMap.Count >= _maxCapacity)
                {
                    evictedValue = Replace(key, val);
                    notifyEviction = true;
                    added = true;
                }
                else
                {
                    LinkedListNode<LruCacheItem> newNode = new(new(key, val));
                    LinkedListNode<LruCacheItem>.AddMostRecent(ref _leastRecentlyUsed, newNode);
                    _cacheMap.Add(key, newNode);
                    added = true;
                }
            }

            if (notifyEviction)
            {
                NotifyEvicted(evictedValue);
            }

            return added;
        }

        public bool Delete(TKey key)
        {
            TValue evictedValue = default!;
            bool removed;
            using (McsLock.Disposable lockRelease = _lock.Acquire())
            {
                removed = DeleteNoLock(key, out evictedValue);
            }

            if (removed)
            {
                NotifyEvicted(evictedValue);
            }

            return removed;
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
                RemoveNoLock(key, node);
                return true;
            }

            value = default;
            return false;
        }

        private bool DeleteNoLock(TKey key, out TValue evictedValue)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                evictedValue = node.Value.Value;
                RemoveNoLock(key, node);
                return true;
            }

            evictedValue = default!;
            return false;
        }

        private void RemoveNoLock(TKey key, LinkedListNode<LruCacheItem> node)
        {
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

        public TValue[] GetValues()
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            int i = 0;
            TValue[] array = new TValue[_cacheMap.Count];
            foreach (KeyValuePair<TKey, LinkedListNode<LruCacheItem>> kvp in _cacheMap)
            {
                array[i++] = kvp.Value.Value.Value;
            }

            return array;
        }

        public int Count => _cacheMap.Count;

        protected virtual void Evict(TValue value)
        {
        }

        private TValue Replace(TKey key, TValue value)
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
            return evictedValue;

            [DoesNotReturn]
            static void ThrowInvalidOperationException() => throw new InvalidOperationException(
                    $"{nameof(LruCache<TKey, TValue>)} called {nameof(Replace)} when empty.");
        }

        private void NotifyEvictedValues(TValue[]? evictedValues)
        {
            if (evictedValues is null)
            {
                return;
            }

            for (int i = 0; i < evictedValues.Length; i++)
            {
                NotifyEvicted(evictedValues[i]);
            }
        }

        private void NotifyEvicted(TValue value)
        {
            if (value is not null)
            {
                Evict(value);
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
