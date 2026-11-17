// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public interface ITxFilterPipeline
    {
        public void AddTxFilter(ITxFilter txFilter);

        /// <summary>Runs the filters for the block <paramref name="context"/> describes, passing the context to each.</summary>
        bool Execute(Transaction tx, in TxFilterContext context);
    }
}
