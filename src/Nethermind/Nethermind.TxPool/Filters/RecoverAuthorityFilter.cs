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
            if (tx.HasAuthorizationList)
            {
                foreach (AuthorizationTuple tuple in tx.AuthorizationList)
                {
                    tuple.Authority ??= ecdsa.RecoverAddress(tuple);
                }
            }

            return AcceptTxResult.Accepted;
        }
    }
}
