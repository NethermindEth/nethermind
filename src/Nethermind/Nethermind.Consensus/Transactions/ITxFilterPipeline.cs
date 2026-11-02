// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Transactions
{
    public interface ITxFilterPipeline
    {
        public void AddTxFilter(ITxFilter txFilter);

        bool Execute(Transaction tx, BlockHeader parentHeader, IReleaseSpec spec);

        /// <summary>Runs the filters for the block <paramref name="context"/> describes, passing the context to each.</summary>
        /// <remarks>The default runs <see cref="Execute(Transaction, BlockHeader, IReleaseSpec)"/>.</remarks>
        bool Execute(Transaction tx, in TxFilterContext context) => Execute(tx, context.Parent, context.Spec);
    }
}
