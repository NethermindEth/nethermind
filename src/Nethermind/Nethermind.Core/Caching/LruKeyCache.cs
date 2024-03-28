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
    using System.Xml.Linq;

    public sealed class LruKeyCache<TKey> : IThreadPoolWorkItem where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly string _name;
        private readonly ConcurrentDictionary<TKey, LinkedListNode<TKey>> _cacheMap;
        private readonly ConcurrentQueue<LinkedListNode<TKey>> _accesses;
        private LinkedListNode<TKey>? _leastRecentlyUsed;
        private int _doingWork;

        public LruKeyCache(int maxCapacity, int startCapacity, string name)
        {
            _maxCapacity = maxCapacity;
            _accesses = new ConcurrentQueue<LinkedListNode<TKey>>();
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new ConcurrentDictionary<TKey, LinkedListNode<TKey>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new ConcurrentDictionary<TKey, LinkedListNode<TKey>>(); // do not initialize it at the full capacity
        }

        public LruKeyCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        public void Clear()
        {
            _cacheMap.Clear();
            _accesses.Clear();
            _leastRecentlyUsed = null;
        }

        public bool Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                Schedule(node);
                return true;
            }

            return false;
        }

        public bool Set(TKey key)
        {
            bool isNew = false;
            LinkedListNode<TKey> node = _cacheMap.GetOrAdd(
                key, (TKey key) =>
                {
                    isNew = true;
                    return new(key);
                });

            if (!isNew)
            {
                node.Value = key;
            }

            Schedule(node);
            return isNew;
        }

        public void Delete(TKey key)
        {
            if (_cacheMap.TryRemove(key, out LinkedListNode<TKey>? node))
            {
                Schedule(node);
            }
        }

        private void Schedule(LinkedListNode<TKey> node)
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
                while (_accesses.TryDequeue(out LinkedListNode<TKey>? node))
                {
                    if (!_cacheMap.ContainsKey(node.Value))
                    {
                        LinkedListNode<TKey>.Remove(ref _leastRecentlyUsed, node);
                    }
                    else if (node.Next is null)
                    {
                        LinkedListNode<TKey>.AddMostRecent(ref _leastRecentlyUsed, node);
                    }
                    else
                    {
                        LinkedListNode<TKey>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                    }
                }

                while (_cacheMap.Count > _maxCapacity)
                {
                    LinkedListNode<TKey> leastRecentlyUsed = _leastRecentlyUsed!;
                    _cacheMap.TryRemove(leastRecentlyUsed.Value, out _);
                    LinkedListNode<TKey>.Remove(ref _leastRecentlyUsed, leastRecentlyUsed);
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

        public long MemorySize => CalculateMemorySize(0, _cacheMap.Count);

        // TODO: memory size on the KeyCache will be smaller because we do not create LruCacheItems
        public static long CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
        {
            // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)

            const int preInit = 48 /* LinkedList */ + 80 /* Dictionary */ + 24;
            int postInit = 52 /* lazy init of two internal dictionary arrays + dictionary size times (entry size + int) */ + MemorySizes.FindNextPrime(currentItemsCount) * 28 + currentItemsCount * 80 /* LinkedListNode and CacheItem times items count */;
            return MemorySizes.Align(preInit + postInit + keyPlusValueSize * currentItemsCount);
        }
    }
}
