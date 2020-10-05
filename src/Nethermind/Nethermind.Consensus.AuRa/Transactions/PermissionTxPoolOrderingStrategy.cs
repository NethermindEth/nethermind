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
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PermissionTxPoolOrderingStrategy : TxPoolTxSource.ITxPoolOrderStrategy
    {
        private readonly IContractDataStore<Address> _sendersWhitelist;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities;

        public PermissionTxPoolOrderingStrategy(
            IContractDataStore<Address> sendersWhitelist, // expected HashSet based
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities) // expected SortedList based
        {
            _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
            _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
        }
        
        public IEnumerable<Transaction> Order(BlockHeader blockHeader, IEnumerable<Transaction> transactions)
        {
            UInt256 GetPriority(Transaction tx) =>
                _priorities.TryGetValue(blockHeader, tx, out var destination)
                    ? destination.Value
                    : UInt256.Zero;
            
            ISet<Address> sendersWhitelist = _sendersWhitelist.GetItemsFromContractAtBlock(blockHeader).AsSet();
            IComparer<Transaction> transactionComparer = new TransactionComparer(
                t => sendersWhitelist.Contains(t.SenderAddress), 
                GetPriority);
            
            // We group transactions by sender. Each group is sorted by nonce and then by priority desc, then gasprice desc, then gaslimit asc 
            // transactions grouped by sender with nonce order then priority:
            // A -> N0_P3, N1_P1, N1_P0, N3_P5...
            // B -> N4_P4, N5_P3, N6_P3...
            IEnumerable<IEnumerable<Transaction>> bySenderOrdered = transactions
                .Where(tx => tx != null) // for safety
                .GroupBy(tx => tx.SenderAddress)
                .Select(g => g.OrderBy(tx => tx.Nonce).ThenBy(t => t, transactionComparer));

            return Order(bySenderOrdered, transactionComparer);
        }
        
        private IEnumerable<Transaction> Order(
            IEnumerable<IEnumerable<Transaction>> transactionsBySenderOrderedByNonce, 
            IComparer<Transaction> comparer)
        {
            IEnumerator<Transaction>[] bySenderEnumerators = transactionsBySenderOrderedByNonce
                .Select(g => g.GetEnumerator())
                .ToArray();
            
            try
            {
                // we create a sorted list of head of each group of transactions. From:
                // A -> N0_P3, N1_P1, N1_P0, N3_P5...
                // B -> N4_P4, N5_P3, N6_P3...
                // We construct [N4_P4 (B), N0_P3 (A)] in sorted order by priority
                var transactions = new DictionarySet<Transaction, IEnumerator<Transaction>>(comparer);
            
                for (int i = 0; i < bySenderEnumerators.Length; i++)
                {
                    IEnumerator<Transaction> enumerator = bySenderEnumerators[i];
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }
                }

                // while there are still unreturned transactions
                while (transactions.Count > 0)
                {
                    // we take first transaction from sorting order, on first call: N4_P4 from B
                    var (tx, enumerator) = transactions.Min;

                    // we replace it by next transaction from same sender, on first call N5_P3 from B
                    transactions.Remove(tx);
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }

                    // we return transactions in lazy manner, no need to sort more than will be taken into block
                    yield return tx;
                }
            }
            finally
            {
                // disposing enumerators
                for (int i = 0; i < bySenderEnumerators.Length; i++)
                {
                    bySenderEnumerators[i].Dispose();
                }
            }
        }
        
        private class TransactionComparer : IComparer<Transaction>
        {
            private readonly Func<Transaction, bool> _isWhiteListed;
            private readonly Func<Transaction, UInt256> _getPriority;

            public TransactionComparer(Func<Transaction, bool> isWhiteListed, Func<Transaction, UInt256> getPriority)
            {
                _isWhiteListed = isWhiteListed ?? throw new ArgumentNullException(nameof(isWhiteListed));
                _getPriority = getPriority ?? throw new ArgumentNullException(nameof(getPriority));
            }

            public int Compare(Transaction x, Transaction y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (ReferenceEquals(null, y)) return 1;
                if (ReferenceEquals(null, x)) return -1;
                
                // we already have nonce ordered by previous code, we don't deal with it here
                
                // first order by whitelisted
                int whitelistedComparision = _isWhiteListed(y).CompareTo(_isWhiteListed(x));
                if (whitelistedComparision != 0) return whitelistedComparision;
                
                // then order by priority descending
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
