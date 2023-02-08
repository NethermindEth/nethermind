// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    public sealed class LruKeyCache<TKey> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly string _name;
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _cacheMap;
        private LinkedListNode<TKey>? _leastRecentlyUsed;

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
            _leastRecentlyUsed = null;
            _cacheMap.Clear();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                MoveToMostRecent(node);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Set(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                MoveToMostRecent(node);
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
                    LinkedListNode<TKey> newNode = new(key);
                    AddMostRecent(newNode);
                    _cacheMap.Add(key, newNode);
                }

                return true;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Delete(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                Remove(node);
                _cacheMap.Remove(key);
            }
        }

        private void MoveToMostRecent(LinkedListNode<TKey> node)
        {
            if (node.Next == node)
            {
                Debug.Assert(_cacheMap.Count == 1 && _leastRecentlyUsed == node, "this should only be true for a list with only one node");
                // Do nothing only one node
            }
            else
            {
                Remove(node);
                AddMostRecent(node);
            }
        }

        private void AddMostRecent(LinkedListNode<TKey> node)
        {
            if (_leastRecentlyUsed is null)
            {
                SetFirst(node);
            }
            else
            {
                InsertMostRecent(node);
            }
        }

        private void InsertMostRecent(LinkedListNode<TKey> newNode)
        {
            LinkedListNode<TKey> first = _leastRecentlyUsed!;
            newNode.Next = first;
            newNode.Prev = first.Prev;
            first.Prev!.Next = newNode;
            first.Prev = newNode;
        }

        private void SetFirst(LinkedListNode<TKey> newNode)
        {
            Debug.Assert(_leastRecentlyUsed is null && _cacheMap.Count == 0, "LinkedList must be empty when this method is called!");
            newNode.Next = newNode;
            newNode.Prev = newNode;
            _leastRecentlyUsed = newNode;
        }

        private void Remove(LinkedListNode<TKey> node)
        {
            Debug.Assert(_leastRecentlyUsed is not null, "This method shouldn't be called on empty list!");
            if (node.Next == node)
            {
                Debug.Assert(_cacheMap.Count == 1 && _leastRecentlyUsed == node, "this should only be true for a list with only one node");
                _leastRecentlyUsed = null;
            }
            else
            {
                node.Next!.Prev = node.Prev;
                node.Prev!.Next = node.Next;
                if (_leastRecentlyUsed == node)
                {
                    _leastRecentlyUsed = node.Next;
                }
            }
        }

        private void Replace(TKey key)
        {
            // TODO: some potential null ref issue here?

            LinkedListNode<TKey>? node = _leastRecentlyUsed;
            if (node is null)
            {
                throw new InvalidOperationException(
                    $"{nameof(LruKeyCache<TKey>)} called {nameof(Replace)} when empty.");
            }

            _cacheMap.Remove(node.Value);
            node.Value = key;
            MoveToMostRecent(node);
            _cacheMap.Add(key, node);
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
