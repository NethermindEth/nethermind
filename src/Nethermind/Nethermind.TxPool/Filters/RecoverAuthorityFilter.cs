// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Will recover authority from transactions with authority_list and filter any with bad signatures.
    /// /// </summary>
    internal sealed class RecoverAuthorityFilter(IEthereumEcdsa ecdsa, ILogger logger) : IIncomingTxFilter
    {
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (!tx.HasAuthorizationList)
                return AcceptTxResult.Accepted;

            Metrics.PendingTransactionsWithExpensiveFiltering++;

            foreach (AuthorizationTuple tuple in tx.AuthorizationList!)
            {
                if (tuple is null)
                {
                    //Should not happen in production
                    continue;
                }
                tuple.Authority ??= ecdsa.RecoverAddress(tuple);
                if (tuple.Authority is null)
                {
                    if (logger.IsTrace) logger.Trace($"Skipped adding transaction because of bad authority signature {tx.ToString("  ")}");
                    return AcceptTxResult.FailedToResolveAuthority;
                }
            }

            return AcceptTxResult.Accepted;
        }
    }
}
