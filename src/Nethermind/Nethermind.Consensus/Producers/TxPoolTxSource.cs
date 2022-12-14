// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

[assembly: InternalsVisibleTo("Nethermind.AuRa.Test")]

namespace Nethermind.Consensus.Producers
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
            ITransactionComparerProvider? transactionComparerProvider,
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
            IEip1559Spec specFor1559 = _specProvider.GetSpecFor1559(blockNumber);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parent, specFor1559);
            IDictionary<Address, Transaction[]> pendingTransactions = _transactionPool.GetPendingTransactionsBySender();
            IComparer<Transaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, blockNumber))
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

            IEnumerable<Transaction> transactions = GetOrderedTransactions(pendingTransactions, comparer);
            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at block gas limit {gasLimit}.");

            int selectedTransactions = 0;
            int i = 0;

            // TODO: removing transactions from TX pool here seems to be a bad practice since they will
            // not come back if the block is ignored?
            int blobsCounter = 0;
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
                if (success)
                {
                    if (tx.Type == TxType.Blob)
                    {
                        if ((blobsCounter + tx.BlobVersionedHashes.Length) > 4)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Declining {tx.ToShortString()}, no more blob space.");
                            continue;
                        }
                        else
                        {
                            blobsCounter += tx.BlobVersionedHashes.Length;
                            if (_logger.IsTrace) _logger.Trace($"Including blob tx {tx.ToShortString()}, total blobs included: {blobsCounter}.");
                        }
                    }
                    if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be potentially included in block.");

                    selectedTransactions++;
                    yield return tx;
                }
            }

            if (_logger.IsDebug) _logger.Debug($"Potentially selected {selectedTransactions} out of {i} pending transactions checked.");

        }

        protected virtual IEnumerable<Transaction> GetOrderedTransactions(IDictionary<Address, Transaction[]> pendingTransactions, IComparer<Transaction> comparer) =>
            Order(pendingTransactions, comparer);

        protected virtual IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext)
            => _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        internal static IEnumerable<Transaction> Order(IDictionary<Address, Transaction[]> pendingTransactions, IComparer<Transaction> comparerWithIdentity)
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
