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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PermissionTxPoolSelectionStrategy : TxPoolTxSource.ITxPoolSelectionStrategy
    {
        private readonly IContractDataStore<Address> _sendersWhitelist;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities;

        public PermissionTxPoolSelectionStrategy(
            IContractDataStore<Address> sendersWhitelist, 
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities)
        {
            // _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
            _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));;
        }
        
        public IEnumerable<Transaction> Select(BlockHeader blockHeader, IEnumerable<Transaction> transactions)
        {
            IEnumerable<Address> sendersWhitelist = _sendersWhitelist.GetItems(blockHeader);
            IComparer<Transaction> transactionComparer = new TransactionComparer(t => GetPriority(t, blockHeader));
            
            // transactions grouped by sender with nonce order:
            // A -> 0, 1, 3...
            // B -> 4, 5, 6...
            IEnumerable<IGrouping<Address, Transaction>> bySenderOrderedNonce = transactions
                .Where(tx => tx != null) // for safety
                .OrderBy(tx => tx.Nonce)
                .ThenBy(t => t, transactionComparer)
                .GroupBy(tx => tx.SenderAddress);

            // partitioned into 2 groups: whitelisted and not whitelisted
            IGrouping<bool, IGrouping<Address, Transaction>>[] byWhitelist = bySenderOrderedNonce
                .GroupBy(g => sendersWhitelist.Contains(g.Key))
                .OrderByDescending(g => g.Key)
                .ToArray();

            IEnumerable<IGrouping<Address, Transaction>> whitelisted = byWhitelist.First();
            IEnumerable<IGrouping<Address, Transaction>> notWhitelisted = byWhitelist.Last();

            return Order(whitelisted, transactionComparer).Concat(Order(notWhitelisted, transactionComparer));
        }

        private UInt256 GetPriority(Transaction transaction, BlockHeader blockHeader) => 
            _priorities.TryGetValue(blockHeader, transaction, out var destination) 
                ? destination.Value 
                : UInt256.Zero;

        private IEnumerable<Transaction> Order(IEnumerable<IGrouping<Address, Transaction>> transactionsBySenderOrderedByNonce, IComparer<Transaction> comparer)
        {
            IEnumerator<Transaction>[] bySenderEnumerators = transactionsBySenderOrderedByNonce
                .Select(g => g.GetEnumerator())
                .ToArray();
            
            try
            {
                SortedDictionary<Transaction, IEnumerator<Transaction>> transactions = new SortedDictionary<Transaction, IEnumerator<Transaction>>(comparer);
            
                for (int i = 0; i < bySenderEnumerators.Length; i++)
                {
                    IEnumerator<Transaction> enumerator = bySenderEnumerators[i];
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }
                }

                while (transactions.Count > 0)
                {
                    var (tx, enumerator) = transactions.First();
                    transactions.Remove(tx);
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }

                    yield return tx;
                }
            }
            finally
            {
                for (int i = 0; i < bySenderEnumerators.Length; i++)
                {
                    bySenderEnumerators[i].Dispose();
                }
            }
        }
        
        private class TransactionComparer : IComparer<Transaction>
        {
            private readonly Func<Transaction, UInt256> _getPriority;

            public TransactionComparer(Func<Transaction, UInt256> getPriority)
            {
                _getPriority = getPriority ?? throw new ArgumentNullException(nameof(getPriority));
            }


            public int Compare(Transaction x, Transaction y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (ReferenceEquals(null, y)) return 1;
                if (ReferenceEquals(null, x)) return -1;
                
                // we already have nonce ordered by previous code, we don't deal with it here
                // first order by priority descending
                int priorityComparision = _getPriority(y).CompareTo(_getPriority(x));
                if (priorityComparision != 0) return priorityComparision;
                
                // then by gas price descending
                int gasPriceComparison = y.GasPrice.CompareTo(x.GasPrice);
                if (gasPriceComparison != 0) return gasPriceComparison;
                
                // then by gas limit ascending
                return x.GasLimit.CompareTo(y.GasLimit);
            }
        }
    }
}
