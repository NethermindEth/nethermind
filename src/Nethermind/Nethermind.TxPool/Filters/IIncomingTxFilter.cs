// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filter used for discarding inbound transactions in the TX pool.
    /// Name used to differentiate from filters from Consensus namespace.
    /// </summary>
    public interface IIncomingTxFilter
    {
        AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions);
    }
}
