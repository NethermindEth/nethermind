// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching
{
    public sealed class LruKeyCache<TKey> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly string _name;
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _cacheMap;
        private readonly McsLock _lock = new();
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

        public void Clear()
        {
            using var lockRelease = _lock.Acquire();

            _leastRecentlyUsed = null;
            _cacheMap.Clear();
        }

        public bool Get(TKey key)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                LinkedListNode<TKey>.MoveToMostRecent(ref _leastRecentlyUsed, node);
                return true;
            }

            return false;
        }

        public bool Set(TKey key)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                LinkedListNode<TKey>.MoveToMostRecent(ref _leastRecentlyUsed, node);
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
                    LinkedListNode<TKey>.AddMostRecent(ref _leastRecentlyUsed, newNode);
                    _cacheMap.Add(key, newNode);
                }

                return true;
            }
        }

        public void Delete(TKey key)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                LinkedListNode<TKey>.Remove(ref _leastRecentlyUsed, node);
                _cacheMap.Remove(key);
            }
        }

        private void Replace(TKey key)
        {
            LinkedListNode<TKey>? node = _leastRecentlyUsed;
            if (node is null)
            {
                ThrowInvalidOperation();
            }

            _cacheMap.Remove(node.Value);
            node.Value = key;
            LinkedListNode<TKey>.MoveToMostRecent(ref _leastRecentlyUsed, node);
            _cacheMap.Add(key, node);

            [DoesNotReturn]
            static void ThrowInvalidOperation()
            {
                throw new InvalidOperationException(
                                    $"{nameof(LruKeyCache<TKey>)} called {nameof(Replace)} when empty.");
            }
        }
    }
}
