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

namespace Nethermind.TxPool.Collections
{
    public class TxDistinctSortedPool : DistinctValueSortedPool<Keccak, WrappedTransaction, Address>
    {
        public TxDistinctSortedPool(int capacity, IComparer<WrappedTransaction> comparer, ILogManager logManager) 
            : base(capacity, comparer, CompetingTransactionEqualityComparer.Instance, logManager)
        {
        }

        protected override IComparer<WrappedTransaction> GetUniqueComparer(IComparer<WrappedTransaction> comparer) => comparer.GetPoolUniqueTxComparer();
        protected override IComparer<WrappedTransaction> GetGroupComparer(IComparer<WrappedTransaction> comparer) => comparer.GetPoolUniqueTxComparerByNonce();

        protected override Address? MapToGroup(WrappedTransaction value) => value.Tx.MapTxToGroup();
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdatePool(Func<Address, ICollection<WrappedTransaction>, IEnumerable<(WrappedTransaction Tx, Action<WrappedTransaction> Change)>> changingElements)
        {
            foreach ((Address groupKey, ICollection<WrappedTransaction> bucket) in _buckets)
            {
                UpdateGroup(groupKey, bucket, changingElements);
            }
        }
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void UpdateGroup(Address groupKey, Func<Address, ICollection<WrappedTransaction>, IEnumerable<(WrappedTransaction Tx, Action<WrappedTransaction> Change)>> changingElements)
        {
            if (groupKey == null) throw new ArgumentNullException(nameof(groupKey));
            if (_buckets.TryGetValue(groupKey, out ICollection<WrappedTransaction> bucket))
            {
                UpdateGroup(groupKey, bucket, changingElements);
            }
        }
        
        private void UpdateGroup(Address groupKey, ICollection<WrappedTransaction> bucket, Func<Address, ICollection<WrappedTransaction>, IEnumerable<(WrappedTransaction Tx, Action<WrappedTransaction> Change)>> changingElements)
        {
            foreach (var elementChanged in changingElements(groupKey, bucket))
            {
                UpdateElement(elementChanged.Tx, elementChanged.Change);
            }
        }

        private void UpdateElement(WrappedTransaction wTx, Action<WrappedTransaction> change)
        {
            if (_sortedValues.Remove(wTx))
            {
                change(wTx);
                _sortedValues.Add(wTx, wTx.Tx.Hash);
            }
        }
    }
}
