// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching
{
    public sealed class LruCache<TKey, TValue> : IThreadPoolWorkItem, ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly ReadWriteLockDisposable _lock = new();
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly ConcurrentQueue<LinkedListNode<LruCacheItem>> _accesses;
        private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;
        private CancellationTokenSource _cts;
        private int _doingWork;

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _maxCapacity = maxCapacity;
            _accesses = new ConcurrentQueue<LinkedListNode<LruCacheItem>>();
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity, (IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new Dictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity);
            _cts = new CancellationTokenSource();
        }

        public LruCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        public void Clear()
        {
            // Signal background thread to stop
            _cts.Cancel();

            using var handle = _lock.AcquireWrite();

            // Signal background thread to stop (two ways)
            _leastRecentlyUsed = null;
            _accesses.Clear();
            _cacheMap.Clear();
            _cts = new CancellationTokenSource();
            // reclear incase background thread set it before accesses were cleared
            _leastRecentlyUsed = null;
        }

        public TValue Get(TKey key)
        {
            bool success = false;
            LinkedListNode<LruCacheItem>? node;

            using (var handle = _lock.AcquireRead())
            {
                success = _cacheMap.TryGetValue(key, out node);
            }

            if (success)
            {
                TValue value = node!.Value.Value;
                Schedule(node);
                return value;
            }

            return default!;
        }

        public bool TryGet(TKey key, out TValue value)
        {
            bool success = false;
            LinkedListNode<LruCacheItem>? node;

            using (var handle = _lock.AcquireRead())
            {
                success = _cacheMap.TryGetValue(key, out node);
            }

            if (success)
            {
                value = node!.Value.Value;
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

            ref LinkedListNode<LruCacheItem>? node = ref Unsafe.NullRef<LinkedListNode<LruCacheItem>?>();
            bool exists = false;
            using (var handle = _lock.AcquireWrite())
            {
                node = ref CollectionsMarshal.GetValueRefOrAddDefault(_cacheMap, key, out exists);
            }

            if (node is not null)
            {
                node.Value.Value = val;
            }
            else
            {
                node = new LinkedListNode<LruCacheItem>(new(key, val));
            }

            Schedule(node);
            return !exists;
        }

        public bool Delete(TKey key)
        {
            LinkedListNode<LruCacheItem>? node = null;
            bool exists = false;
            using (var handle = _lock.AcquireWrite())
            {
                exists = _cacheMap.Remove(key, out node);
            }

            if (exists)
            {
                Schedule(node!);
            }

            return exists;
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
            CancellationToken ct = _cts.Token;
            while (true)
            {
                while (!ct.IsCancellationRequested && _accesses.TryDequeue(out LinkedListNode<LruCacheItem>? node))
                {
                    bool exists = false;
                    using (var handle = _lock.AcquireRead())
                    {
                        exists = _cacheMap.ContainsKey(node.Value.Key);
                    }

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
                    LinkedListNode<LruCacheItem>? leastRecentlyUsed;
                    using (var handle = _lock.AcquireWrite())
                    {
                        leastRecentlyUsed = _leastRecentlyUsed;
                        if (leastRecentlyUsed is null)
                        {
                            // Clear has been called
                            break;
                        }

                        _cacheMap.Remove(leastRecentlyUsed.Value.Key, out leastRecentlyUsed);
                    }

                    LinkedListNode<LruCacheItem>.Remove(ref _leastRecentlyUsed, leastRecentlyUsed!);
                }

                // All work done.

                // Set _doingWork (0 == false) prior to checking IsEmpty to catch any missed work in interim.
                // This doesn't need to be volatile due to the following barrier (i.e. it is volatile).
                _doingWork = 0;

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
