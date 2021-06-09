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
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections
{
    public class TxDistinctSortedPool : DistinctValueSortedPool<Keccak, Transaction, Address>
    {
        private readonly IComparer<Transaction> _comparer;
        private readonly ILogger _logger;

        public TxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager) 
            : base(capacity, comparer, CompetingTransactionEqualityComparer.Instance, logManager)
        {
            _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override IComparer<Transaction> GetUniqueComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparer();
        protected override IComparer<Transaction> GetGroupComparer(IComparer<Transaction> comparer) => comparer.GetPoolUniqueTxComparerByNonce();

        protected override Address? MapToGroup(Transaction value) => value.MapTxToGroup();
        
        protected override bool CanInsert(Keccak hash, Transaction transaction)
        {
            // either there is no distinct value or it would go before (or at same place) as old value
            // if it would go after old value in order, we ignore it and wont add it
            if (base.CanInsert(hash, transaction))
            {
                bool isDuplicate = _distinctDictionary.TryGetValue(transaction, out var oldKvp);
                if (isDuplicate)
                {
                    Transaction oldTx = oldKvp.Value;
                    Transaction oldTxWithPrice10PercentHigher = new Transaction();
                    
                    oldTx.GasBottleneck.Divide(10, out UInt256 bumpGasBottleneck);
                    oldTxWithPrice10PercentHigher.GasBottleneck = oldTx.GasBottleneck + bumpGasBottleneck;
                    
                    oldTx.GasPrice.Divide(10, out UInt256 bumpGasPrice);
                    oldTxWithPrice10PercentHigher.GasPrice = oldTx.GasPrice + bumpGasPrice;

                    bool isHigher = _comparer.Compare(transaction, oldTxWithPrice10PercentHigher) <= 0;
                    
                    if (_logger.IsTrace && !isHigher)
                    {
                        _logger.Trace($"Cannot insert {nameof(Transaction)} {transaction}, its not distinct and not higher than old {nameof(Transaction)} {oldKvp.Value} by more than 10%.");
                    }

                    return isHigher;
                }

                return true;
            }

            return false;
        }
        
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
