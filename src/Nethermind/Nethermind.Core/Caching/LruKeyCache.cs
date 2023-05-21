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
    public sealed class LruKeyCache<TKey> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly string _name;
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _cacheMap;
        private LinkedListNode<TKey>? _singleAccessLru;
        private LinkedListNode<TKey>? _multiAccessLru;

        public int SingleAccessCount { get; private set; }
        public int MultiAccessCount { get; private set; }

        public LruKeyCache(int maxCapacity, int startCapacity, string name)
        {
            _maxCapacity = maxCapacity;
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, LinkedListNode<TKey>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new Dictionary<TKey, LinkedListNode<TKey>>(startCapacity); // do not initialize it at the full capacity
        }

        public LruKeyCache(int maxCapacity, string name)
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

        public bool Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                ulong accessCount = node.AccessCount;
                LinkedListNode<TKey>.MoveToMostRecent(ref _singleAccessLru, ref _multiAccessLru, node);
                if (accessCount == 1)
                {
                    SingleAccessCount--;
                    MultiAccessCount++;
                }
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Set(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                ulong accessCount = node.AccessCount;
                LinkedListNode<TKey>.MoveToMostRecent(ref _singleAccessLru, ref _multiAccessLru, node);
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
                    Replace(key);
                }
                else
                {
                    SingleAccessCount++;
                    LinkedListNode<TKey> newNode = new(key);
                    LinkedListNode<TKey>.AddMostRecent(ref _singleAccessLru, newNode);
                    _cacheMap.Add(key, newNode);
                }

                return true;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Delete(TKey key)
        {
            if (_cacheMap.Remove(key, out LinkedListNode<TKey>? node))
            {
                if (node.AccessCount == 1)
                {
                    SingleAccessCount--;
                    LinkedListNode<TKey>.Remove(ref _singleAccessLru, node);
                }
                else
                {
                    MultiAccessCount--;
                    LinkedListNode<TKey>.Remove(ref _multiAccessLru, node);
                }
            }
        }

        private void Replace(TKey key)
        {
            LinkedListNode<TKey>? node;
            if (MultiAccessCount > _maxCapacity / 2 || _singleAccessLru is null)
            {
                MultiAccessCount--;
                node = _multiAccessLru;
            }
            else
            {
                SingleAccessCount--;
                node = _singleAccessLru;
            }

            if (node is null)
            {
                ThrowInvalidOperationException();
            }

            if (!_cacheMap.Remove(node.Value))
            {
                ThrowInvalidOperationException();
            }

            node.Value = key;
            node.AccessCount = 1;

            LinkedListNode<TKey>.AddMostRecent(ref _singleAccessLru, node);
            _cacheMap.Add(key, node);

            [DoesNotReturn]
            static void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException(
                                    $"{nameof(LruKeyCache<TKey>)} called {nameof(Replace)} when empty.");
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
