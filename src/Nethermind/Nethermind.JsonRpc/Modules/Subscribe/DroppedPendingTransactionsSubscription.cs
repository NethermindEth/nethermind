// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class DroppedPendingTransactionsSubscription : Subscription
    {
        private readonly ITxPool _txPool;
        private readonly Func<object?, Task> _send;

        public DroppedPendingTransactionsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ITxPool? txPool,
            ILogManager? logManager)
            : base(jsonRpcDuplexClient, MaxQueuedEvents)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _logger = logManager?.GetClassLogger<DroppedPendingTransactionsSubscription>() ?? throw new ArgumentNullException(nameof(logManager));

            _send = Send;
            _txPool.EvictedPending += OnEvicted;
            if (_logger.IsTrace) _logger.Trace($"DroppedPendingTransactions subscription {Id} will track DroppedPendingTransactions");
        }

        /// <remarks>
        /// Queues just the hash, so a lagging subscriber does not keep evicted transactions alive.
        /// </remarks>
        private void OnEvicted(object? sender, TxEventArgs e)
        {
            if (e.Transaction is not { } transaction) return;
            ScheduleAction(_send, transaction.Hash);
        }

        private async Task Send(object? hash)
        {
            using JsonRpcResult result = CreateSubscriptionMessage((Hash256)hash!);
            await JsonRpcDuplexClient.SendJsonRpcResult(result);
            if (_logger.IsTrace)
                _logger.Trace(
                    $"DroppedPendingTransactions subscription {Id} printed hash of DroppedPendingTransaction.");
        }

        public override string Type => SubscriptionType.EthSubscription.DroppedPendingTransactions;

        public override void Dispose()
        {
            _txPool.EvictedPending -= OnEvicted;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"DroppedPendingTransactions subscription {Id} will no longer track DroppedPendingTransactions");
        }
    }
}
