// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class NewPendingTransactionsSubscription : Subscription
    {
        private readonly ITxPool _txPool;
        private readonly ISpecProvider _specProvider;
        private readonly bool _includeTransactions;

        public NewPendingTransactionsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            ITxPool? txPool,
            ISpecProvider? specProvider,
            ILogManager? logManager,
            TransactionsOption? options = null)
            : base(jsonRpcDuplexClient)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _includeTransactions = options?.IncludeTransactions ?? false;

            _txPool.NewPending += OnNewPending;
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will track NewPendingTransactions");
        }

        private void OnNewPending(object? sender, TxEventArgs e)
        {
            ScheduleAction(async () =>
            {
                using JsonRpcResult result = CreateSubscriptionMessage(_includeTransactions
                    ? TransactionForRpc.FromTransaction(e.Transaction, chainId: _specProvider.ChainId)
                    : e.Transaction.Hash!, "eth_subscription");
                await JsonRpcDuplexClient.SendJsonRpcResult(result);
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
