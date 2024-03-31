// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    using NonBlocking;

    public sealed class NonBlockingLruCache<TKey, TValue> : IThreadPoolWorkItem, ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly ConcurrentDictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly ConcurrentQueue<LinkedListNode<LruCacheItem>> _accesses;

        private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;
        private CancellationTokenSource _cts;

        private long _toProcess;
        private int _processingLru;

        public NonBlockingLruCache(int maxCapacity, int startCapacity, string name)
        {
            // Both types of concurrent dictionary heavily allocate if key is larger than pointer size.
            ArgumentOutOfRangeException.ThrowIfGreaterThan(Unsafe.SizeOf<TKey>(), IntPtr.Size);

            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _maxCapacity = maxCapacity;
            _accesses = new ConcurrentQueue<LinkedListNode<LruCacheItem>>();
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new ConcurrentDictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new ConcurrentDictionary<TKey, LinkedListNode<LruCacheItem>>();
            _cts = new CancellationTokenSource();
        }

        public NonBlockingLruCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        public void Clear()
        {
            // Signal background thread to stop
            _cts.Cancel();
            // Signal background thread to stop (two ways)
            _leastRecentlyUsed = null;
            _accesses.Clear();
            _cacheMap.Clear();
            _cts = new CancellationTokenSource();
            // reclear incase background thread set it before accesses were cleared
            _leastRecentlyUsed = null;
        }

        public TValue Get(TKey key) => TryGet(key, out TValue value) ? value : default!;

        public bool TryGet(TKey key, out TValue value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node!.Value.Value;
                ScheduleLruAccounting(node);
                return true;
            }

            value = default!;
            return false;
        }

        public void Set(TKey key, TValue val)
        {
            if (val is null)
            {
                Delete(key);
            }

            LinkedListNode<LruCacheItem> node = _cacheMap.AddOrUpdate(
                key,
                (k, v) => new(new(k, v)),
                (k, node, v) =>
                {
                    node.Value.Value = val;
                    return node;
                },
                val);

            ScheduleLruAccounting(node, isGet: false);
        }

        bool ICache<TKey, TValue>.Set(TKey key, TValue val) => SetResult(key, val);

        public bool SetResult(TKey key, TValue val)
        {
            if (val is null)
            {
                return Delete(key);
            }

            bool exists = false; // Captured by lambda
            LinkedListNode<LruCacheItem> node = _cacheMap.AddOrUpdate(
                key,
                (k, v) => new(new(k, v)),
                (k, node, v) =>
                {
                    exists = true;
                    node.Value.Value = val;
                    return node;
                },
                val);

            ScheduleLruAccounting(node, isGet: false);
            return !exists;
        }

        public bool Delete(TKey key)
        {
            if (_cacheMap.Remove(key, out LinkedListNode<LruCacheItem>? node))
            {
                ScheduleLruAccounting(node!, isGet: false);
                return true;
            }

            return false;
        }

        public bool Contains(TKey key)
        {
            // Use TryGet to schedule the node for Lru.
            return TryGet(key, out _);
        }

        private void ScheduleLruAccounting(LinkedListNode<LruCacheItem> node, bool isGet = true)
        {
            _accesses.Enqueue(node);

            long count = Interlocked.Increment(ref _toProcess);
            if (count < _maxCapacity && (isGet || _cacheMap.Count < _maxCapacity))
            {
                // Can wait
                return;
            }

            // Set working if it wasn't (via atomic Interlocked).
            if (Interlocked.CompareExchange(ref _processingLru, 1, 0) == 0)
            {
                Volatile.Write(ref _toProcess, 0);
                // Wasn't working, schedule.
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            try
            {
                ProcessExpiredItems();
            }
            catch
            {
                // Tear it all down
                Clear();
                _processingLru = 0;
            }
        }

        private void ProcessExpiredItems()
        {
            CancellationToken ct = _cts.Token;
            while (true)
            {
                while (!ct.IsCancellationRequested && _accesses.TryDequeue(out LinkedListNode<LruCacheItem>? node))
                {
                    bool exists = _cacheMap.ContainsKey(node.Value.Key);

                    ref LinkedListNode<LruCacheItem>? leastRecentlyUsed = ref _leastRecentlyUsed;
                    if (leastRecentlyUsed is null)
                    {
                        // Clear has been called
                        break;
                    }

                    if (!exists)
                    {
                        LinkedListNode<LruCacheItem>.Remove(ref leastRecentlyUsed, node);
                    }
                    else if (node.Next is null)
                    {
                        LinkedListNode<LruCacheItem>.AddMostRecent(ref leastRecentlyUsed, node);
                    }
                    else
                    {
                        LinkedListNode<LruCacheItem>.MoveToMostRecent(ref leastRecentlyUsed, node);
                    }
                }

                while (!ct.IsCancellationRequested && _cacheMap.Count > _maxCapacity)
                {
                    LinkedListNode<LruCacheItem>? leastRecentlyUsed = _leastRecentlyUsed;
                    if (leastRecentlyUsed is null)
                    {
                        // Clear has been called
                        break;
                    }

                    _cacheMap.Remove(leastRecentlyUsed.Value.Key, out leastRecentlyUsed);

                    LinkedListNode<LruCacheItem>.Remove(ref _leastRecentlyUsed, leastRecentlyUsed!);
                }

                // All work done.

                // Set _doingWork (0 == false) prior to checking IsEmpty to catch any missed work in interim.
                // This doesn't need to be volatile due to the following barrier (i.e. it is volatile).
                _processingLru = 0;

                // Ensure _doingWork is written before IsEmpty is read.
                // As they are two different memory locations, we insert a barrier to guarantee ordering.
                Thread.MemoryBarrier();

                // Check if there is work to do
                if (ct.IsCancellationRequested || _accesses.IsEmpty)
                {
                    // Nothing to do, exit.
                    break;
                }

                // Is work, can we set it as active again (via atomic Interlocked), prior to scheduling?
                if (Interlocked.Exchange(ref _processingLru, 1) == 1)
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
