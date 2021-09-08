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
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Comparers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

[assembly:InternalsVisibleTo("Nethermind.AuRa.Test")]

namespace Nethermind.Blockchain.Producers
{
    public class TxPoolTxSource : ITxSource
    {
        private readonly ITxPool _transactionPool;
        private readonly ITransactionComparerProvider _transactionComparerProvider;
        private readonly ITxFilterPipeline _txFilterPipeline;
        private readonly ISpecProvider _specProvider;
        protected readonly ILogger _logger;

        public TxPoolTxSource(
            ITxPool? transactionPool, 
            ISpecProvider? specProvider,
            ITransactionComparerProvider transactionComparerProvider, 
            ILogManager? logManager,
            ITxFilterPipeline? txFilterPipeline)
        {
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _transactionComparerProvider = transactionComparerProvider ?? throw new ArgumentNullException(nameof(transactionComparerProvider));
            _txFilterPipeline = txFilterPipeline ?? throw new ArgumentNullException(nameof(txFilterPipeline));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger<TxPoolTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            long blockNumber = parent.Number + 1;
            IReleaseSpec releaseSpec = _specProvider.GetSpec(blockNumber);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parent, releaseSpec);
            IDictionary<Address, Transaction[]> pendingTransactions = _transactionPool.GetPendingTransactionsBySender();
            IComparer<Transaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, blockNumber))
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not loose transactions we need to differentiate on their identity which provided comparer might not be doing
            
            IEnumerable<Transaction> transactions = GetOrderedTransactions(pendingTransactions, comparer);
            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at block gas limit {gasLimit}.");

            int selectedTransactions = 0;
            int i = 0;
            
            // TODO: removing transactions from TX pool here seems to be a bad practice since they will
            // not come back if the block is ignored?
            foreach (Transaction tx in transactions)
            {
                i++;
                
                if (tx.SenderAddress is null)
                {
                    _transactionPool.RemoveTransaction(tx.Hash!);
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (null sender) {tx.ToShortString()}");
                    continue;
                }

                bool success = _txFilterPipeline.Execute(tx, parent);
                if (!success)
                {
                    _transactionPool.RemoveTransaction(tx.Hash!);
                    continue;
                }
                
                if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be potentially included in block.");
                
                selectedTransactions++;
                yield return tx;
            }

            if (_logger.IsDebug) _logger.Debug($"Potentially selected {selectedTransactions} out of {i} pending transactions checked.");
            
        }
        
        protected virtual IEnumerable<Transaction> GetOrderedTransactions(IDictionary<Address,Transaction[]> pendingTransactions, IComparer<Transaction> comparer) => 
            Order(pendingTransactions, comparer);

        protected virtual IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext) 
            => _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        internal static IEnumerable<Transaction> Order(IDictionary<Address,Transaction[]> pendingTransactions, IComparer<Transaction> comparerWithIdentity)
        {
            IEnumerator<Transaction>[] bySenderEnumerators = pendingTransactions
                .Select<KeyValuePair<Address, Transaction[]>, IEnumerable<Transaction>>(g => g.Value)
                .Select(g => g.GetEnumerator())
                .ToArray();
            
            try
            {
                // we create a sorted list of head of each group of transactions. From:
                // A -> N0_P3, N1_P1, N1_P0, N3_P5...
                // B -> N4_P4, N5_P3, N6_P3...
                // We construct [N4_P4 (B), N0_P3 (A)] in sorted order by priority
                DictionarySortedSet<Transaction, IEnumerator<Transaction>> transactions = new(comparerWithIdentity);
            
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
                    (Transaction tx, IEnumerator<Transaction> enumerator) = transactions.Min;

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

        public override string ToString() => $"{nameof(TxPoolTxSource)}";
    }
}
