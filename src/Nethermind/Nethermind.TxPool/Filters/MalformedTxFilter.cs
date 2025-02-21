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
    internal sealed class MalformedTxFilter(
        IChainHeadSpecProvider specProvider,
        ITxValidator txValidator,
        ILogger logger)
        : IIncomingTxFilter
    {
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            IReleaseSpec spec = specProvider.GetCurrentHeadSpec();
            ValidationResult result = txValidator.IsWellFormed(tx, spec);
            if (!result)
            {
                Metrics.PendingTransactionsMalformed++;
                // It may happen that other nodes send us transactions that were signed for another chain or don't have enough gas.
                if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, invalid transaction: {result}");
                return AcceptTxResult.Invalid.WithMessage($"{result}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
