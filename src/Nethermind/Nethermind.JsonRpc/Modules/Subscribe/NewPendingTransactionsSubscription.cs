// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class NewPendingTransactionsSubscription : Subscription
    {
        private readonly ITxPool _txPool;
        private readonly PendingTransactionPayloadCache _payloads;
        private readonly bool _includeTransactions;

        [ConstructorWithSideEffect]
        public NewPendingTransactionsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ITxPool? txPool,
            ISpecProvider? specProvider,
            ILogManager? logManager,
            TransactionsOption? options = null)
            : this(jsonRpcDuplexClient, txPool, new PendingTransactionPayloadCache(specProvider ?? throw new ArgumentNullException(nameof(specProvider))), logManager, options)
        {
        }

        [ConstructorWithSideEffect]
        internal NewPendingTransactionsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ITxPool? txPool,
            PendingTransactionPayloadCache payloads,
            ILogManager? logManager,
            TransactionsOption? options = null)
            : base(jsonRpcDuplexClient, MaxQueuedEvents)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _payloads = payloads;
            _logger = logManager?.GetClassLogger<NewPendingTransactionsSubscription>() ?? throw new ArgumentNullException(nameof(logManager));
            _includeTransactions = options?.IncludeTransactions ?? false;

            _txPool.NewPending += OnNewPending;
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will track NewPendingTransactions");
        }

        private void OnNewPending(object? sender, TxEventArgs e) => ScheduleAction(async () =>
        {
            using JsonRpcResult result = _includeTransactions
                ? CreateSubscriptionMessage(_payloads.Get(e.Transaction))
                : CreateSubscriptionMessage(e.Transaction.Hash!);
            await JsonRpcDuplexClient.SendJsonRpcResult(result);
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} printed hash of NewPendingTransaction.");
        });

        public override string Type => SubscriptionType.EthSubscription.NewPendingTransactions;

        public override void Dispose()
        {
            _txPool.NewPending -= OnNewPending;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will no longer track NewPendingTransactions");
        }
    }
}
