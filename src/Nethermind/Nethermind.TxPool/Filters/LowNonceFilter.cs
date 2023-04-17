// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions where nonce is lower than the current sender account nonce.
    /// </summary>
    internal sealed class LowNonceFilter : IIncomingTxFilter
    {
        private readonly ILogger _logger;

        public LowNonceFilter(ILogger logger)
        {
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            // As we have limited number of transaction that we store in mem pool its fairly easy to fill it up with
            // high-priority garbage transactions. We need to filter them as much as possible to use the tx pool space
            // efficiently. One call to get account from state is not that costly and it only happens after previous checks.
            // This was modeled by OpenEthereum behavior.
            Account account = state.SenderAccount;
            UInt256 currentNonce = account.Nonce;
            if (tx.Nonce < currentNonce)
            {
                Metrics.PendingTransactionsLowNonce++;
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce already used.");
                }

                bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
                return isNotLocal ?
                    AcceptTxResult.OldNonce :
                    AcceptTxResult.OldNonce.WithMessage($"Current nonce: {currentNonce}, nonce of rejected tx: {tx.Nonce}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
