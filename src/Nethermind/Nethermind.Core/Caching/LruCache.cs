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
    public class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, int> _cacheMap;
        private readonly McsLock _lock = new();
        private readonly string _name;
        private readonly LruSlots<LruCacheItem> _slots;
        private readonly bool _notifyEviction;

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _name = name;
            _maxCapacity = maxCapacity;
            _slots = new(maxCapacity, Math.Min(startCapacity, maxCapacity));
            // Subclasses may override Evict; retain their notifications outside the lock.
            _notifyEviction = GetType() != typeof(LruCache<TKey, TValue>);
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, int>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new Dictionary<TKey, int>(startCapacity); // do not initialize it at the full capacity
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
                if (_notifyEviction && _cacheMap.Count != 0)
                {
                    int i = 0;
                    evictedValues = new TValue[_cacheMap.Count];
                    foreach (KeyValuePair<TKey, int> kvp in _cacheMap)
                    {
                        evictedValues[i++] = _slots.Value(kvp.Value).Value;
                    }
                }

                _slots.Clear();
                _cacheMap.Clear();
            }

            NotifyEvictedValues(evictedValues);
        }

        public TValue Get(TKey key)
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out int node))
            {
                TValue value = _slots.Value(node).Value;
                _slots.MoveToMostRecent(node);
                return value;
            }

            return default!;
        }

        public bool TryGet(TKey key, out TValue value)
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out int node))
            {
                value = _slots.Value(node).Value;
                _slots.MoveToMostRecent(node);
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
                if (_cacheMap.TryGetValue(key, out int node))
                {
                    TValue value = _slots.Value(node).Value;
                    _slots.MoveToMostRecent(node);
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
                    int newNode = _slots.AddMostRecent(new(key, newValue));
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
                else if (_cacheMap.TryGetValue(key, out int node))
                {
                    evictedValue = _slots.Value(node).Value;
                    notifyEviction = true;
                    _slots.Value(node).Value = val;
                    _slots.MoveToMostRecent(node);
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
                    int newNode = _slots.AddMostRecent(new(key, val));
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

            if (_cacheMap.TryGetValue(key, out int node))
            {
                value = _slots.Value(node).Value;
                RemoveNoLock(key, node);
                return true;
            }

            value = default;
            return false;
        }

        private bool DeleteNoLock(TKey key, out TValue evictedValue)
        {
            if (_cacheMap.TryGetValue(key, out int node))
            {
                evictedValue = _slots.Value(node).Value;
                RemoveNoLock(key, node);
                return true;
            }

            evictedValue = default!;
            return false;
        }

        private void RemoveNoLock(TKey key, int node)
        {
            _slots.Remove(node);
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
            foreach (KeyValuePair<TKey, int> kvp in _cacheMap)
            {
                array[i++] = new KeyValuePair<TKey, TValue>(kvp.Key, _slots.Value(kvp.Value).Value);
            }

            return array;
        }

        public TValue[] GetValues()
        {
            using McsLock.Disposable lockRelease = _lock.Acquire();

            int i = 0;
            TValue[] array = new TValue[_cacheMap.Count];
            foreach (KeyValuePair<TKey, int> kvp in _cacheMap)
            {
                array[i++] = _slots.Value(kvp.Value).Value;
            }

            return array;
        }

        public int Count => _cacheMap.Count;

        protected virtual void Evict(TValue value)
        {
        }

        private TValue Replace(TKey key, TValue value)
        {
            int node = _slots.LeastRecentlyUsed;
            if (node < 0)
            {
                ThrowInvalidOperationException();
            }

            TValue evictedValue = _slots.Value(node).Value;
            _cacheMap.Remove(_slots.Value(node).Key);

            _slots.Value(node) = new(key, value);
            _slots.MoveToMostRecent(node);
            _cacheMap.Add(key, node);
            return evictedValue;

            [DoesNotReturn]
            static void ThrowInvalidOperationException() => throw new InvalidOperationException(
                    $"{nameof(LruCache<,>)} called {nameof(Replace)} when empty.");
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

        public long MemorySize => CalculateMemorySize(0, _slots.Capacity);

        public static long CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
        {
            // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)

            const int preInit = 48 /* Slots */ + 80 /* Dictionary */ + 64;
            long postInit = 72 /* Three array headers */
                + (long)MemorySizes.FindNextPrime(currentItemsCount) * (12 + Unsafe.SizeOf<TKey>())
                + (long)currentItemsCount * LruSlots<LruCacheItem>.EntrySize;
            return MemorySizes.Align(preInit + postInit + keyPlusValueSize * currentItemsCount);
        }
    }
}
