// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    using NonBlocking;

    public sealed class LruCache<TKey, TValue> : IThreadPoolWorkItem, ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly ConcurrentDictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly ConcurrentQueue<LinkedListNode<LruCacheItem>> _accesses;
        private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;
        private int _doingWork;

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _maxCapacity = maxCapacity;
            _accesses = new ConcurrentQueue<LinkedListNode<LruCacheItem>>();
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new ConcurrentDictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new ConcurrentDictionary<TKey, LinkedListNode<LruCacheItem>>();
        }

        public LruCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        public void Clear()
        {
            _cacheMap.Clear();
            _accesses.Clear();
            _leastRecentlyUsed = null;
        }

        public TValue Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                TValue value = node.Value.Value;
                Schedule(node);
                return value;
            }

            return default!;
        }

        public bool TryGet(TKey key, out TValue value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                Schedule(node);
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

            bool isNew = false;
            LinkedListNode<LruCacheItem> node = _cacheMap.GetOrAdd(
                key,
                (TKey key, TValue value) =>
                {
                    isNew = true;
                    return new(new(key, value));
                },
                val);

            if (!isNew)
            {
                node.Value.Value = val;
            }

            Schedule(node);
            return isNew;
        }

        public bool Delete(TKey key)
        {
            if (_cacheMap.TryRemove(key, out LinkedListNode<LruCacheItem>? node))
            {
                Schedule(node);
                return true;
            }

            return false;
        }

        public bool Contains(TKey key)
        {
            // Use TryGet to schedule the node for Lru.
            return TryGet(key, out _);
        }

        private void Schedule(LinkedListNode<LruCacheItem> node)
        {
            _accesses.Enqueue(node);

            // Set working if it wasn't (via atomic Interlocked).
            if (Interlocked.CompareExchange(ref _doingWork, 1, 0) == 0)
            {
                // Wasn't working, schedule.
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            while (true)
            {
                while (_accesses.TryDequeue(out LinkedListNode<LruCacheItem>? node))
                {
                    if (!_cacheMap.ContainsKey(node.Value.Key))
                    {
                        LinkedListNode<LruCacheItem>.Remove(ref _leastRecentlyUsed, node);
                    }
                    else if (node.Next is null)
                    {
                        LinkedListNode<LruCacheItem>.AddMostRecent(ref _leastRecentlyUsed, node);
                    }
                    else
                    {
                        LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                    }
                }

                while (_cacheMap.Count > _maxCapacity)
                {
                    LinkedListNode<LruCacheItem> leastRecentlyUsed = _leastRecentlyUsed!;
                    if (leastRecentlyUsed is null)
                    {
                        break;
                    }
                    _cacheMap.TryRemove(leastRecentlyUsed.Value.Key, out _);
                    LinkedListNode<LruCacheItem>.Remove(ref _leastRecentlyUsed, leastRecentlyUsed);
                }

                // All work done.

                // Set _doingWork (0 == false) prior to checking IsEmpty to catch any missed work in interim.
                // This doesn't need to be volatile due to the following barrier (i.e. it is volatile).
                _doingWork = 0;

                // Ensure _doingWork is written before IsEmpty is read.
                // As they are two different memory locations, we insert a barrier to guarantee ordering.
                Thread.MemoryBarrier();

                // Check if there is work to do
                if (_accesses.IsEmpty)
                {
                    // Nothing to do, exit.
                    break;
                }

                // Is work, can we set it as active again (via atomic Interlocked), prior to scheduling?
                if (Interlocked.Exchange(ref _doingWork, 1) == 1)
                {
                    // Execute has been rescheduled already, exit.
                    break;
                }

                // Is work, wasn't already scheduled so continue loop.
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
