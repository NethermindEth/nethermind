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
using System.Linq;
using System.Runtime.CompilerServices;

namespace Nethermind.Core
{
    public class SortedPool<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Comparison<TValue> _comparison;
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _cacheMap;
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _lruList;

        public SortedPool(int capacity, Comparison<TValue> comparison)
        {
            _capacity = capacity;
            _comparison = comparison;
            _cacheMap = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(); // do not initialize it at the full capacity
            _lruList = new LinkedList<KeyValuePair<TKey, TValue>>();
        }

        public int Count => _cacheMap.Count;

        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue[] GetSnapshot()
        {
            return _lruList.Select(i => i.Value).ToArray();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public TValue TakeFirst()
        {
            var value = _lruList.First.Value;
            _lruList.RemoveFirst();
            _cacheMap.Remove(value.Key);
            return value.Value;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryRemove(TKey key, out TValue tx)
        {
            if (_cacheMap.TryGetValue(key, out var txNode))
            {
                if (_cacheMap.Remove(key))
                {
                    _lruList.Remove(txNode);
                    tx = txNode.Value.Value;
                    return true;
                }
            }

            tx = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGetValue(TKey key, out TValue tx)
        {
            if (_cacheMap.TryGetValue(key, out var txNode))
            {
                tx = txNode.Value.Value;
                return true;
            }

            tx = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryInsert(TKey key, TValue val)
        {
            if (val == null)
            {
                throw new ArgumentNullException();
            }

            if (_cacheMap.TryGetValue(key, out _))
            {
                return false;
            }

            KeyValuePair<TKey, TValue> cacheItem = new KeyValuePair<TKey, TValue>(key, val);
            LinkedListNode<KeyValuePair<TKey, TValue>> newNode = new LinkedListNode<KeyValuePair<TKey, TValue>>(cacheItem);


            LinkedListNode<KeyValuePair<TKey, TValue>> node = _lruList.First;
            bool added = false;
            while (node != null)
            {
                if (_comparison(node.Value.Value, val) < 0)
                {
                    _lruList.AddBefore(node, newNode);
                    added = true;
                    break;
                }

                node = node.Next;
            }

            if (!added)
            {
                _lruList.AddLast(newNode);
            }

            _cacheMap.Add(key, newNode);

            if (_cacheMap.Count > _capacity)
            {
                RemoveLast();
            }

            return true;
        }

        private void RemoveLast()
        {
            LinkedListNode<KeyValuePair<TKey, TValue>> node = _lruList.Last;
            _lruList.RemoveLast();

            _cacheMap.Remove(node.Value.Key);
        }
    }
}