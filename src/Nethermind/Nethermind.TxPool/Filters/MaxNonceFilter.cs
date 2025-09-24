// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions where nonce exceeds 2^64-1 (EIP-2681).
    /// </summary>
    internal sealed class MaxNonceFilter : IIncomingTxFilter
    {
        private readonly ILogger _logger;

        public MaxNonceFilter(ILogger logger)
        {
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (tx.Nonce > Transaction.MaxNonce)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce {tx.Nonce} exceeds maximum allowed value {Transaction.MaxNonce}.");
                }

                return AcceptTxResult.NonceTooHigh;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
