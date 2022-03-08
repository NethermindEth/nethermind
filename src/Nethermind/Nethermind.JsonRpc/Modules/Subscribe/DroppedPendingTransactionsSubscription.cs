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
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class DroppedPendingTransactionsSubscription : Subscription
    {
        private readonly ITxPool _txPool;

        public DroppedPendingTransactionsSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, ITxPool? txPool, ILogManager? logManager) : base(jsonRpcDuplexClient)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _txPool.EvictedPending += OnEvicted;
            if(_logger.IsTrace) _logger.Trace($"DroppedPendingTransactions subscription {Id} will track DroppedPendingTransactions");
        }

        private void OnEvicted(object? sender, TxEventArgs e)
        {
            ScheduleAction(() =>
            {
                JsonRpcResult result = CreateSubscriptionMessage(e.Transaction.Hash);
                JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"DroppedPendingTransactions subscription {Id} printed hash of DroppedPendingTransaction.");
            });
        }

        public override string Type => SubscriptionType.DroppedPendingTransactions;

        public override void Dispose()
        {
            _txPool.EvictedPending -= OnEvicted;
            base.Dispose();
            if(_logger.IsTrace) _logger.Trace($"DroppedPendingTransactions subscription {Id} will no longer track DroppedPendingTransactions");
        }
    }
}
