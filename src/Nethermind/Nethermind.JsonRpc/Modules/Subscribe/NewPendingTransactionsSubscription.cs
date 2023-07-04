// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class NewPendingTransactionsSubscription : Subscription
    {
        private readonly ITxPool _txPool;
        private readonly bool _includeTransactions;

        public NewPendingTransactionsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ITxPool? txPool,
            ILogManager? logManager,
            TransactionsOption? options = null)
            : base(jsonRpcDuplexClient)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _includeTransactions = options?.IncludeTransactions ?? false;

            _txPool.NewPending += OnNewPending;
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will track NewPendingTransactions");
        }

        private void OnNewPending(object? sender, TxEventArgs e)
        {
            ScheduleAction(() =>
            {
                JsonRpcResult result = CreateSubscriptionMessage(_includeTransactions ? new TransactionForRpc(e.Transaction) : e.Transaction.Hash);
                JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} printed hash of NewPendingTransaction.");
            });
        }

        public override string Type => SubscriptionType.NewPendingTransactions;

        public override void Dispose()
        {
            _txPool.NewPending -= OnNewPending;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will no longer track NewPendingTransactions");
        }
    }
}
