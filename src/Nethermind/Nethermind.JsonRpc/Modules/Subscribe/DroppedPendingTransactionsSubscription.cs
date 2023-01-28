// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class DroppedPendingTransactionsSubscription : Subscription
    {
        private readonly ITxPool _txPool;

        public DroppedPendingTransactionsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ITxPool? txPool,
            ILogManager? logManager)
            : base(jsonRpcDuplexClient)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _txPool.EvictedPending += OnEvicted;
            if (_logger.IsTrace) _logger.Trace($"DroppedPendingTransactions subscription {Id} will track DroppedPendingTransactions");
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
            if (_logger.IsTrace) _logger.Trace($"DroppedPendingTransactions subscription {Id} will no longer track DroppedPendingTransactions");
        }
    }
}
