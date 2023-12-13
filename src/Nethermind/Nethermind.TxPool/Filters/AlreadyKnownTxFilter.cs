// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// This filters out transactions that have already been analyzed in the current scope
    /// (block scope for rejected transactions or chain scope for accepted transactions).
    /// It uses a limited capacity hash cache underneath so there is no strict promise on filtering
    /// transactions.
    /// </summary>
    internal sealed class AlreadyKnownTxFilter : IIncomingTxFilter
    {
        private readonly HashCache _hashCache;
        private readonly ILogger _logger;

        public AlreadyKnownTxFilter(
            HashCache hashCache,
            ILogger logger)
        {
            _hashCache = hashCache;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (_hashCache.Get(tx.Hash!))
            {
                if (_logger.IsTrace) _logger.Trace($"Found tx in _hashCache. TxHash: {tx?.Hash}, Tx: {tx}");
                Metrics.PendingTransactionsKnown++;
                return AcceptTxResult.AlreadyKnown;
            }

            _hashCache.SetForCurrentBlock(tx.Hash!);

            return AcceptTxResult.Accepted;
        }
    }
}
