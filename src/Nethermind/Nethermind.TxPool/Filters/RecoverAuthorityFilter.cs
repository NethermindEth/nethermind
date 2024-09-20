// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Will recover authority from transactions with authority_list
    /// /// </summary>
    internal sealed class RecoverAuthorityFilter(IEthereumEcdsa ecdsa) : IIncomingTxFilter
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
            }

            return AcceptTxResult.Accepted;
        }
    }
}
