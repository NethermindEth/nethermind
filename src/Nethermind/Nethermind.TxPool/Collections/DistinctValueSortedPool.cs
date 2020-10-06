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

namespace Nethermind.TxPool.Collections
{
    public class DistinctValueSortedPool<TKey, TValue, TGroup> : SortedPool<TKey, TValue, TGroup>
    {
        private readonly IDictionary<TValue, KeyValuePair<TKey, TValue>> _distinctDictionary;

        public DistinctValueSortedPool(
            int capacity,
            IComparer<TValue> comparer,
            Func<TValue, TGroup> groupMapping,
            IEqualityComparer<TValue> distinctComparer) 
            : base(capacity, comparer, groupMapping)
        {
            _distinctDictionary = new Dictionary<TValue, KeyValuePair<TKey, TValue>>(distinctComparer);
        }

        protected override void InsertCore(TKey key, TValue value, ICollection<TValue> collection)
        {
            lock (_distinctDictionary)
            {
                if (_distinctDictionary.TryGetValue(value, out var oldKvp))
                {
                    TryRemove(oldKvp.Key, out _);
                }

                base.InsertCore(key, value, collection);
                _distinctDictionary[value] = new KeyValuePair<TKey, TValue>(key, value);
            }
        }
        
        protected override bool Remove(TKey key, TValue value)
        {
            lock (_distinctDictionary)
            {
                _distinctDictionary.Remove(value);
                return base.Remove(key, value);
            }
        }

        protected override bool CanInsert(TKey key, TValue value)
        {
            lock (_distinctDictionary)
            {
                // either there is no distinct value or it would go before (or at same place) as old value
                // if it would go after old value in order, we ignore it and wont add it
                return base.CanInsert(key, value)
                       && (!_distinctDictionary.TryGetValue(value, out var oldKvp) || Comparer.Compare(oldKvp.Value, value) >= 0);
            }
        }

    }
}
