// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that were generated at this machine and were already signed with the same nonce.
    /// TODO: review if cancel by replace is still possible with this!
    /// </summary>
    internal class ReusedOwnNonceTxFilter : IIncomingTxFilter
    {
        private readonly INonceManager _nonceManager;
        private readonly ILogger _logger;

        public ReusedOwnNonceTxFilter(INonceManager nonceManager, ILogger logger)
        {
            _nonceManager = nonceManager;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            bool managedNonce = (handlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;

            if (managedNonce && _nonceManager.IsNonceUsed(tx.SenderAddress!, tx.Nonce))
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce already used.");
                return AcceptTxResult.OwnNonceAlreadyUsed;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
