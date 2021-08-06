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
using Nethermind.Logging;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Collections
{
    public class TxDistinctSortedPool : DistinctValueSortedPool<Keccak, Transaction, Address>
    {
        public TxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager) 
            : base(capacity, comparer, CompetingTransactionEqualityComparer.Instance, logManager)
        {
        }

        protected override IComparer<Transaction> GetUniqueComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparer();
        protected override IComparer<Transaction> GetGroupComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparerByNonce();
        protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer) => comparer.GetReplacementComparer();
        
        protected override Address? MapToGroup(Transaction value) => value.MapTxToGroup();

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdatePool(Func<Address, ICollection<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction> Change)>> changingElements)
        {
            foreach ((Address groupKey, ICollection<Transaction> bucket) in _buckets)
            {
                UpdateGroup(groupKey, bucket, changingElements);
            }
        }
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdateGroup(Address groupKey, Func<Address, ICollection<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction> Change)>> changingElements)
        {
            if (groupKey == null) throw new ArgumentNullException(nameof(groupKey));
            if (_buckets.TryGetValue(groupKey, out ICollection<Transaction> bucket))
            {
                UpdateGroup(groupKey, bucket, changingElements);
            }
        }
        
        private void UpdateGroup(Address groupKey, ICollection<Transaction> bucket, Func<Address, ICollection<Transaction>, IEnumerable<(Transaction Tx, Action<Transaction> Change)>> changingElements)
        {
            foreach (var elementChanged in changingElements(groupKey, bucket))
            {
                UpdateElement(elementChanged.Tx, elementChanged.Change);
            }
        }

        private void UpdateElement(Transaction tx, Action<Transaction> change)
        {
            if (_sortedValues.Remove(tx))
            {
                change(tx);
                _sortedValues.Add(tx, tx.Hash);
            }
        }
    }
}
