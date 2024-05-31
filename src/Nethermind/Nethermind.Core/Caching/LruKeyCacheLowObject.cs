// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching
{
    public sealed class LruKeyCacheLowObject<TKey>
        where TKey : struct, IEquatable<TKey>
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, int> _cacheMap;
        private readonly McsLock _lock = new();
        private readonly string _name;
        private int _leastRecentlyUsed = -1;
        private Stack<int> _freeOffsets = new();
        private readonly LruCacheItem[] _items;

        public LruKeyCacheLowObject(int maxCapacity, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _name = name;
            _maxCapacity = maxCapacity;
            _cacheMap = new Dictionary<TKey, int>(maxCapacity / 2); // do not initialize it at the full capacity
            _items = new LruCacheItem[maxCapacity];
        }

        public void Clear()
        {
            using var lockRelease = _lock.Acquire();

            _leastRecentlyUsed = -1;
            _cacheMap.Clear();
            _freeOffsets.Clear();
            if (RuntimeHelpers.IsReferenceOrContainsReferences<LruCacheItem>())
            {
                _items.AsSpan().Clear();
            }
        }

        public bool Get(TKey key)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out int offset))
            {
                ref var node = ref _items[offset];
                MoveToMostRecent(ref node, offset);
                return true;
            }

            return false!;
        }

        public bool Set(TKey key)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out int offset))
            {
                ref var node = ref _items[offset];
                MoveToMostRecent(ref node, offset);
                return false;
            }

            if (_cacheMap.Count >= _maxCapacity)
            {
                Replace(key);
            }
            else
            {
                if (_freeOffsets.Count > 0)
                {
                    offset = _freeOffsets.Pop();
                }
                else
                {
                    offset = _cacheMap.Count;
                }
                ref LruCacheItem newNode = ref _items[offset];
                newNode = new(key);
                AddMostRecent(ref newNode, offset);
                _cacheMap.Add(key, offset);
            }

            return true;
        }

        public void Delete(TKey key)
        {
            using var lockRelease = _lock.Acquire();

            DeleteNoLock(key);
        }

        private bool DeleteNoLock(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out int offset))
            {
                ref var node = ref _items[offset];
                Remove(ref node, offset);
                _cacheMap.Remove(key);
                node = default;
                _freeOffsets.Push(offset);
                return true;
            }

            return false;
        }

        public bool Contains(TKey key)
        {
            using var lockRelease = _lock.Acquire();

            return _cacheMap.ContainsKey(key);
        }

        public int Size
        {
            get
            {
                return _cacheMap.Count;
            }
        }

        private void Replace(TKey key)
        {
            if (_leastRecentlyUsed < 0)
            {
                ThrowInvalidOperationException();
            }

            int offset = _leastRecentlyUsed;
            ref var node = ref _items[offset];

            _cacheMap.Remove(node.Key);

            MoveToMostRecent(ref node, offset);
            node = new(key);
            _cacheMap.Add(key, offset);

            [DoesNotReturn]
            static void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException($"Called {nameof(Replace)} when empty.");
            }
        }

        private void MoveToMostRecent(ref LruCacheItem node, int offset)
        {
            if (node.Next == offset)
            {
                Debug.Assert(_leastRecentlyUsed == offset, "this should only be true for a list with only one node");
                // Do nothing only one node
            }
            else
            {
                Remove(ref node, offset);
                AddMostRecent(ref node, offset);
            }
        }

        private void Remove(ref LruCacheItem node, int offset)
        {
            Debug.Assert(_leastRecentlyUsed >= 0, "This method shouldn't be called on empty list!");
            if (node.Next == offset)
            {
                Debug.Assert(_leastRecentlyUsed == offset, "this should only be true for a list with only one node");
                _leastRecentlyUsed = -1;
            }
            else
            {
                _items[node.Next].Prev = node.Prev;
                _items[node.Prev].Next = node.Next;
                if (_leastRecentlyUsed == offset)
                {
                    _leastRecentlyUsed = node.Next;
                }
            }
        }

        private void AddMostRecent(ref LruCacheItem node, int offset)
        {
            if (_leastRecentlyUsed < 0)
            {
                SetFirst(ref node, offset);
            }
            else
            {
                InsertMostRecent(ref node, offset);
            }
        }

        private void InsertMostRecent(ref LruCacheItem newNode, int offset)
        {
            newNode.Next = _leastRecentlyUsed;
            ref var node = ref _items[_leastRecentlyUsed];
            newNode.Prev = node.Prev;
            _items[node.Prev].Next = offset;
            node.Prev = offset;
        }

        private void SetFirst(ref LruCacheItem newNode, int offset)
        {
            newNode.Next = offset;
            newNode.Prev = offset;
            _leastRecentlyUsed = offset;
        }

        private struct LruCacheItem(TKey k)
        {
            public readonly TKey Key = k;
            public int Next;
            public int Prev;
        }
    }
}
