// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class TxPriorityTxSource(
        ITxPool transactionPool,
        ILogManager logManager,
        ITxFilterPipeline txFilterPipeline,
        IContractDataStore<Address> sendersWhitelist, // expected HashSet based
        IDictionaryContractDataStore<TxPriorityContract.Destination> priorities,
        ISpecProvider specProvider,
        ITransactionComparerProvider transactionComparerProvider, // expected SortedList based
        IBlocksConfig blocksConfig)
        : TxPoolTxSource(transactionPool, specProvider, transactionComparerProvider, logManager, txFilterPipeline,
            blocksConfig)
    {
        private readonly IContractDataStore<Address> _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
        private CompareTxByPriorityOnSpecifiedBlock _comparer = null!;

        protected override IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext)
        {
            _comparer = new CompareTxByPriorityOnSpecifiedBlock(_sendersWhitelist, _priorities, parent);
            return _comparer.ThenBy(base.GetComparer(parent, blockPreparationContext));
        }

        public override string ToString() => $"{nameof(TxPriorityTxSource)}";

        protected override IEnumerable<Transaction> GetOrderedTransactions(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, long gasLimit)
        {
            if (_logger.IsTrace)
            {
                var transactions = base.GetOrderedTransactions(pendingTransactions, comparer, filter, gasLimit).ToArray();
                string txString = string.Join(Environment.NewLine, transactions.Select(t => $"{t.ToShortString()}, PoolIndex {t.PoolIndex}, Whitelisted: {_comparer.IsWhiteListed(t)}, Priority: {_comparer.GetPriority(t)}"));
                _logger.Trace($"Ordered transactions with comparer {comparer} : {Environment.NewLine}{txString}");
                return transactions;
            }
            return base.GetOrderedTransactions(pendingTransactions, comparer, filter, gasLimit);
        }
    }
}
