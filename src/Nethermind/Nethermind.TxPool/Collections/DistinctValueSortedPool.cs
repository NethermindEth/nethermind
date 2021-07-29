//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections
{
    /// <summary>
    /// Keeps a distinct pool of <see cref="TValue"/> with <see cref="TKey"/> in groups based on <see cref="TGroupKey"/>.
    /// Uses separate comparator to distinct between elements. If there is duplicate element added it uses ordering comparator and keeps the one that is larger. 
    /// </summary>
    /// <typeparam name="TKey">Type of keys of items, unique in pool.</typeparam>
    /// <typeparam name="TValue">Type of items that are kept.</typeparam>
    /// <typeparam name="TGroupKey">Type of groups in which the items are organized</typeparam>
    public abstract class DistinctValueSortedPool<TKey, TValue, TGroupKey> : SortedPool<TKey, TValue, TGroupKey>
    {
        private readonly IComparer<TValue> _comparer;
        private readonly IDictionary<TValue, KeyValuePair<TKey, TValue>> _distinctDictionary;
        private readonly ILogger _logger;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="capacity">Max capacity</param>
        /// <param name="comparer">Comparer to sort items.</param>
        /// <param name="distinctComparer">Comparer to distinct items. Based on this duplicates will be removed.</param>
        /// <param name="logManager">Log manager</param>
        protected DistinctValueSortedPool(
            int capacity,
            IComparer<TValue> comparer,
            IEqualityComparer<TValue> distinctComparer,
            ILogManager logManager) 
            : base(capacity, comparer)
        {
            // ReSharper disable once VirtualMemberCallInConstructor
            _comparer = GetReplacementComparer(comparer ?? throw new ArgumentNullException(nameof(comparer)));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _distinctDictionary = new Dictionary<TValue, KeyValuePair<TKey, TValue>>(distinctComparer);
        }

        protected virtual IComparer<TValue> GetReplacementComparer(IComparer<TValue> comparer) => comparer;

        protected override void InsertCore(TKey key, TValue value, TGroupKey groupKey, ICollection<TValue> bucketCollection)
        {
            if (_distinctDictionary.TryGetValue(value, out KeyValuePair<TKey, TValue> oldKvp))
            {
                TryRemove(oldKvp.Key);
            }
            
            base.InsertCore(key, value, groupKey, bucketCollection);

            _distinctDictionary[value] = new KeyValuePair<TKey, TValue>(key, value);
        }

        protected override bool Remove(TKey key, TValue value)
        {
            _distinctDictionary.Remove(value);
            return base.Remove(key, value);
        }
        
        protected virtual bool AllowSameKeyReplacement => false; 

        protected override bool CanInsert(TKey key, TValue value)
        {
            // either there is no distinct value or it would go before (or at same place) as old value
            // if it would go after old value in order, we ignore it and wont add it
            if (AllowSameKeyReplacement || base.CanInsert(key, value))
            {
                bool isDuplicate = _distinctDictionary.TryGetValue(value, out var oldKvp);
                if (isDuplicate)
                {
                    bool isHigher = _comparer.Compare(value, oldKvp.Value) <= 0;
                    
                    if (_logger.IsTrace && !isHigher)
                    {
                        _logger.Trace($"Cannot insert {nameof(TValue)} {value}, its not distinct and not higher than old {nameof(TValue)} {oldKvp.Value}.");
                    }
                    
                    return isHigher;
                }

                return true;
            }

            return false;
        }
    }
}
