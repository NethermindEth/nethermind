// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that are not well formed (not conforming with the yellowpaper and EIPs)
    /// </summary>
    internal sealed class MalformedTxFilter : IIncomingTxFilter
    {
        private readonly ITxValidator _txValidator;
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly ILogger _logger;

        public MalformedTxFilter(IChainHeadSpecProvider specProvider, ITxValidator txValidator, ILogger logger)
        {
            _txValidator = txValidator;
            _specProvider = specProvider;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            IReleaseSpec spec = _specProvider.GetCurrentHeadSpec();
            if (!_txValidator.IsWellFormed(tx, spec))
            {
                Metrics.PendingTransactionsMalformed++;
                // It may happen that other nodes send us transactions that were signed for another chain or don't have enough gas.
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, invalid transaction.");
                return AcceptTxResult.Invalid;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
