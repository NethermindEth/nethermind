//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching
{
    /// <remarks>
    /// The array based solution is preferred to lower the overall memory management overhead. The <see cref="LinkedListNode{T}"/> based approach is very costly.
    /// </remarks>
    public class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, int> _cacheMap;
        private Node[] _list;

        private const int MinimumListCapacity = 16;

        struct Node
        {
            public const int Null = -1;

            public int Prev;
            public int Next;
            public TValue Value;
            public TKey Key;
        }

        public void Clear()
        {
            _cacheMap?.Clear();
            _list = new Node[MinimumListCapacity];
            _head = Node.Null;
        }

        public LruCache(int maxCapacity, int startCapacity, string name)
        {
            _maxCapacity = maxCapacity;
            _cacheMap = typeof(TKey) == typeof(byte[])
                ? new Dictionary<TKey, int>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
                : new Dictionary<TKey, int>(startCapacity); // do not initialize it at the full capacity
            _list = new Node[Math.Max(startCapacity, MinimumListCapacity)];
        }

        public LruCache(int maxCapacity, string name)
            : this(maxCapacity, 0, name)
        {
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out int node))
            {
                TValue value = _list[node].Value;

                NodeRemove(node);
                NodeAddLast(node);

                return value;
            }

#pragma warning disable 8603
            // fixed C# 9
            return default;
#pragma warning restore 8603
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGet(TKey key, out TValue value)
        {
            if (_cacheMap.TryGetValue(key, out int node))
            {
                value = _list[node].Value;

                NodeRemove(node);
                NodeAddLast(node);

                return true;
            }

#pragma warning disable 8601
            // fixed C# 9
            value = default;
#pragma warning restore 8601
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Set(TKey key, TValue val)
        {
            if (val == null)
            {
                Delete(key);
                return;
            }

            if (_cacheMap.TryGetValue(key, out int node))
            {
                _list[node].Value = val;

                NodeRemove(node);
                NodeAddLast(node);
            }
            else
            {
                if (_cacheMap.Count >= _maxCapacity)
                {
                    Replace(key, val);
                }
                else
                {
                    int newNode = _cacheMap.Count;

                    if (newNode >= _list.Length)
                    {
                        Array.Resize(ref _list, _list.Length * 2);
                    }

                    _list[newNode].Value = val;
                    _list[newNode].Key = key;

                    NodeAddLast(newNode);
                    _cacheMap.Add(key, newNode);
                }
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Delete(TKey key)
        {
            if (_cacheMap.Remove(key, out int node))

            {
                int nextFree = _cacheMap.Count;

                ref Node removed = ref NodeRemove(node, true);

                if (nextFree == 0)
                {
                    // head is null, nothing to remove
                }
                else if (nextFree == node)
                {
                    // nothing to do, the last node was removed
                }
                else
                {
                    removed = _list[nextFree];
                    _list[removed.Prev].Next = node;
                    _list[removed.Next].Prev = node;
                }
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Contains(TKey key) => _cacheMap.ContainsKey(key);

        private void Replace(TKey key, TValue value)
        {
            int i = _head;
            ref Node node = ref NodeRemove(i);
            _cacheMap.Remove(node.Key);

            node.Value = value;
            node.Key = key;
            NodeAddLast(i);

            _cacheMap.Add(key, i);
        }

        private int _head = Node.Null;

        private ref Node NodeRemove(int i, bool cleanValues = false)
        {
            ref Node node = ref _list[i];

            if (node.Next == i)
            {
                _head = Node.Null;
            }
            else
            {
                _list[node.Next].Prev = node.Prev;
                _list[node.Prev].Next = node.Next;

                if (_head == i)
                {
                    _head = node.Next;
                }
            }

            if (cleanValues)
            {
                node.Value = default!;
                node.Key = default!;
            }

            return ref node;
        }

        private void NodeAddLast(int i)
        {
            if (_head == Node.Null)
                NodeInsertToEmptyList(i);
            else
                NodeInsertBefore(_head, i);
        }

        private void NodeInsertToEmptyList(int i)
        {
            ref Node node = ref _list[i];

            node.Next = i;
            node.Prev = i;
            _head = i;
        }

        private void NodeInsertBefore(int n, int @new)
        {
            ref Node node = ref _list[n];
            ref Node newNode = ref _list[@new];

            newNode.Next = n;
            newNode.Prev = node.Prev;
            _list[node.Prev].Next = @new;
            node.Prev = @new;
        }

        public int MemorySize => CalculateMemorySize(0, _cacheMap.Count);

        public static int CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
        {
            // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)

            const int preInit = 48 /* LinkedList */ + 80 /* Dictionary */ + 24;
            int postInit = 52 /* lazy init of two internal dictionary arrays + dictionary size times (entry size + int) */ + MemorySizes.FindNextPrime(currentItemsCount) * 28 + currentItemsCount * 80 /* LinkedListNode and CacheItem times items count */;
            return MemorySizes.Align(preInit + postInit + keyPlusValueSize * currentItemsCount);
        }
    }
}
