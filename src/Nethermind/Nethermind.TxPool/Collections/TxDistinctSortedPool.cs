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
// 

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Collections
{
    public class TxDistinctSortedPool : DistinctValueSortedPool<Keccak, Transaction, Address>
    {
        private readonly List<Transaction> _transactionsToRemove = new();
        
        public TxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager) 
            : base(capacity, comparer, CompetingTransactionEqualityComparer.Instance, logManager)
        {
        }

        protected override IComparer<Transaction> GetUniqueComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparer();
        protected override IComparer<Transaction> GetGroupComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparerByNonce();
        protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer) => comparer.GetReplacementComparer();
        
        protected override Address? MapToGroup(Transaction value) => value.MapTxToGroup();
        protected override Keccak GetKey(Transaction value) => value.Hash!;

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdatePool(Func<Address, ICollection<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction>? Change)>> changingElements)
        {
            foreach ((Address groupKey, SortedSet<Transaction> bucket) in _buckets)
            {
                UpdateGroup(groupKey, bucket, changingElements);
            }
        }
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdateGroup(Address groupKey, Func<Address, ICollection<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction>? Change)>> changingElements)
        {
            if (groupKey == null) throw new ArgumentNullException(nameof(groupKey));
            if (_buckets.TryGetValue(groupKey, out SortedSet<Transaction> bucket))
            {
                UpdateGroup(groupKey, bucket, changingElements);
            }
        }
        
        private void UpdateGroup(Address groupKey, SortedSet<Transaction> bucket, Func<Address, ICollection<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction>? Change)>> changingElements)
        {
            _transactionsToRemove.Clear();
            Transaction? lastElement = bucket.Max;
            
            foreach ((Transaction tx, Action<Transaction>? change) in changingElements(groupKey, bucket))
            {
                if (change is null)
                {
                    _transactionsToRemove.Add(tx);
                }
                else if (Equals(lastElement, tx))
                {
                    bool reAdd = _worstSortedValues.Remove(tx);
                    change(tx);
                    if (reAdd)
                    {
                        _worstSortedValues.Add(tx, tx.Hash);
                    }
                }
                else
                {
                    change(tx);
                }
            }

            for (int i = 0; i < _transactionsToRemove.Count; i++)
            {
                TryRemove(_transactionsToRemove[i].Hash);
            }
        }
    }
}
