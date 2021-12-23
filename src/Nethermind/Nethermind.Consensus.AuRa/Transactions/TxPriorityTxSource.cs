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
using System.Linq;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class TxPriorityTxSource : TxPoolTxSource
    {
        private readonly IContractDataStore<Address> _sendersWhitelist;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities;
        private CompareTxByPriorityOnSpecifiedBlock _comparer;
        
        public TxPriorityTxSource(
            ITxPool transactionPool, 
            IStateReader stateReader, 
            ILogManager logManager, 
            ITxFilterPipeline txFilterPipeline,
            IContractDataStore<Address> sendersWhitelist, // expected HashSet based
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities,
            ISpecProvider specProvider,
            ITransactionComparerProvider transactionComparerProvider) // expected SortedList based
            : base(transactionPool, specProvider, transactionComparerProvider, logManager, txFilterPipeline)
        {
            _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
            _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
        }

        protected override IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext)
        {
            _comparer = new CompareTxByPriorityOnSpecifiedBlock(_sendersWhitelist, _priorities, parent);
            return _comparer.ThenBy(base.GetComparer(parent, blockPreparationContext));
        }

        public override string ToString() => $"{nameof(TxPriorityTxSource)}";

        protected override IEnumerable<Transaction> GetOrderedTransactions(IDictionary<Address, Transaction[]> pendingTransactions, IComparer<Transaction> comparer)
        {
            if (_logger.IsTrace)
            {
                var transactions = base.GetOrderedTransactions(pendingTransactions, comparer).ToArray();
                string txString = string.Join(Environment.NewLine, transactions.Select(t => $"{t.ToShortString()}, PoolIndex {t.PoolIndex}, Whitelisted: {_comparer.IsWhiteListed(t)}, Priority: {_comparer.GetPriority(t)}"));
                _logger.Trace($"Ordered transactions with comparer {comparer} : {Environment.NewLine}{txString}");
                return transactions;
            }
            return base.GetOrderedTransactions(pendingTransactions, comparer);
        }
    }
}
