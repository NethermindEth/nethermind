// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
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
        private readonly Func<object?, Task> _send;
        private int _disposed;

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
            if (_includeTransactions) _payloads.AddSubscriber();
            _send = _includeTransactions ? SendTransaction : SendHash;

            _txPool.NewPending += OnNewPending;
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will track NewPendingTransactions");
        }

        /// <remarks>
        /// A hash-only subscriber queues just the hash, so a lagging one does not keep the transaction, its calldata or
        /// its blob sidecar alive.
        /// </remarks>
        private void OnNewPending(object? sender, TxEventArgs e)
        {
            if (e.Transaction is not { } transaction) return;
            ScheduleAction(_send, _includeTransactions ? transaction : transaction.Hash);
        }

        private Task SendTransaction(object? transaction) => Send(CreateSubscriptionMessage(_payloads.Get((Transaction)transaction!)));

        private Task SendHash(object? hash) => Send(CreateSubscriptionMessage((Hash256)hash!));

        private async Task Send(JsonRpcResult message)
        {
            using JsonRpcResult result = message;
            await JsonRpcDuplexClient.SendJsonRpcResult(result);
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} printed hash of NewPendingTransaction.");
        }

        public override string Type => SubscriptionType.EthSubscription.NewPendingTransactions;

        public override void Dispose()
        {
            _txPool.NewPending -= OnNewPending;
            if (_includeTransactions && Interlocked.Exchange(ref _disposed, 1) == 0) _payloads.RemoveSubscriber();
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"NewPendingTransactions subscription {Id} will no longer track NewPendingTransactions");
        }
    }
}
