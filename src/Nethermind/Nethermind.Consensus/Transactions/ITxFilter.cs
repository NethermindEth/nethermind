// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public interface ITxFilter
    {
        AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec);

        /// <summary>Checks <paramref name="tx"/> for the block <paramref name="context"/> describes.</summary>
        /// <remarks>A filter that needs the next block's base fee overrides this to read
        /// <see cref="TxFilterContext.BaseFee"/>, and a filter that wraps others passes the context on.
        /// The default checks with the parent and spec alone.</remarks>
        AcceptTxResult IsAllowed(Transaction tx, in TxFilterContext context) => IsAllowed(tx, context.Parent, context.Spec);
    }
}
