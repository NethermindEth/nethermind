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
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class NewPendingTransactionsSubscription : Subscription
    {
        private readonly ITxPool _txPool;
        private readonly ILogger _logger;

        public NewPendingTransactionsSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, ITxPool? txPool, ILogManager? logManager) 
            : base(jsonRpcDuplexClient)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            _txPool.NewPending += OnNewPending;
            if(_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will track NewPendingTransactions");
        }

        private void OnNewPending(object? sender, TxEventArgs e)
        {
            Task.Run(() =>
            {
                JsonRpcResult result = CreateSubscriptionMessage(e.Transaction.Hash);
                JsonRpcDuplexClient.SendJsonRpcResult(result);
                if(_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} printed hash of NewPendingTransaction.");
            }).ContinueWith(
                t =>
                    t.Exception?.Handle(ex =>
                    {
                        if (_logger.IsDebug) _logger.Debug($"NewPendingTransactions subscription {Id}: Failed Task.Run after NewPending event.");
                        return true;
                    })
                , TaskContinuationOptions.OnlyOnFaulted
            );
        }

        public override SubscriptionType Type => SubscriptionType.NewPendingTransactions;

        public override void Dispose()
        {
            _txPool.NewPending -= OnNewPending;
            if(_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will no longer track NewPendingTransactions");
        }
    }
}
