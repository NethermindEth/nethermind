// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions with the sender address not resolved properly.
    /// </summary>
    internal sealed class UnknownSenderFilter : IIncomingTxFilter
    {
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ILogger _logger;

        public UnknownSenderFilter(IEthereumEcdsa ecdsa, ILogger logger)
        {
            _ecdsa = ecdsa;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            /* We have encountered multiple transactions that do not resolve sender address properly.
             * We need to investigate what these txs are and why the sender address is resolved to null.
             * Then we need to decide whether we really want to broadcast them.
             */
            Metrics.PendingTransactionsWithExpensiveFiltering++;
            if (tx.SenderAddress is null)
            {
                tx.SenderAddress = _ecdsa.RecoverAddress(tx);
                if (tx.SenderAddress is null)
                {
                    Metrics.PendingTransactionsUnresolvableSender++;

                    if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, no sender.");

                    return AcceptTxResult.FailedToResolveSender;
                }
            }

            return AcceptTxResult.Accepted;
        }
    }
}
