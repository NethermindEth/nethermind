// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public interface ITxFilterPipeline
    {
        public void AddTxFilter(ITxFilter txFilter);

        bool Execute(Transaction tx, BlockHeader parentHeader);
    }
}
