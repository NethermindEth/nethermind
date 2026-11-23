// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public interface ITxFilter
    {
        /// <summary>Checks <paramref name="tx"/> for the block <paramref name="context"/> describes.</summary>
        /// <remarks>A filter that wraps others passes the context on.</remarks>
        AcceptTxResult IsAllowed(Transaction tx, in TxFilterContext context);
    }
}
