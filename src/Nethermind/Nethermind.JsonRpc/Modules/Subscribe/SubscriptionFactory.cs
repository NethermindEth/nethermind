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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class SubscriptionFactory : ISubscriptionFactory
    {
        private readonly ILogManager _logManager;
        private readonly IBlockTree _blockTree;
        private readonly ITxPool _txPool;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IFilterStore _filterStore;

        public SubscriptionFactory(
            ILogManager? logManager,
            IBlockTree? blockTree,
            ITxPool? txPool,
            IReceiptStorage? receiptStorage,
            IFilterStore? filterStore)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
        }
        public Subscription CreateSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, SubscriptionType subscriptionType, Filter? filter)
        {
            switch (subscriptionType)
            {
                case SubscriptionType.NewHeads: 
                    return new NewHeadSubscription(jsonRpcDuplexClient, _blockTree, _logManager);
                case SubscriptionType.Logs:
                    return new LogsSubscription(jsonRpcDuplexClient, _receiptStorage, _filterStore, _blockTree, _logManager, filter);
                case SubscriptionType.NewPendingTransactions:
                    return new NewPendingTransactionsSubscription(jsonRpcDuplexClient, _txPool, _logManager);
                case SubscriptionType.Syncing:
                    return new SyncingSubscription(jsonRpcDuplexClient, _blockTree, _logManager);
                default: throw new Exception("Unexpected SubscriptionType.");
            }
        }
    }
}
