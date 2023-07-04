// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out all the transactions without TX hash calculated.
    /// This generally should never happen as there should be no way for a transaction to be decoded
    /// without hash when coming from devp2p.
    /// </summary>
    internal sealed class NullHashTxFilter : IIncomingTxFilter
    {
        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (tx.Hash is null)
            {
                return AcceptTxResult.Invalid;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
