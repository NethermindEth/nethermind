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

namespace Nethermind.Blockchain.TxPools.Collections
{
    public class DistinctValueSortedPool<TKey, TValue> : SortedPool<TKey, TValue>
    {
        private readonly IDictionary<TValue, LinkedListNode<KeyValuePair<TKey, TValue>>> _distinctDictionary;

        public DistinctValueSortedPool(int capacity, Comparison<TValue> comparison, IEqualityComparer<TValue> distinctComparer) : base(capacity, comparison)
        {
            _distinctDictionary = new Dictionary<TValue, LinkedListNode<KeyValuePair<TKey, TValue>>>(distinctComparer);
        }

        protected override void Add(TKey key, LinkedListNode<KeyValuePair<TKey, TValue>> newNode)
        {
            base.Add(key, newNode);
            var value = newNode.Value.Value;
            
            // if there was a node already with same distinct value we need to remove it
            if (_distinctDictionary.TryGetValue(value, out var oldNode))
            {
                LruList.Remove(oldNode);
                Remove(oldNode.Value.Key);
            }

            _distinctDictionary[value] = newNode;
        }

        protected override bool Remove(TKey key)
        {
            if (CacheMap.TryGetValue(key, out var node))
            {
                _distinctDictionary.Remove(node.Value.Value);
            }
            
            return base.Remove(key);
        }

        protected override bool CanInsert(TKey key, TValue value) =>
            // either there is no distinct value or it would go before (or at same place) as old value
            // if it would go after old value in order, we ignore it and wont add it
            base.CanInsert(key, value) && (!_distinctDictionary.TryGetValue(value, out var oldNode) || Comparison(value, oldNode.Value.Value) >= 0);
        
    }
}