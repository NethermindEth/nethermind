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
    }
}
