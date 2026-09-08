// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

internal class XdcTxFilterPipeline(ITxFilterPipeline inner) : ITxFilterPipeline
{
    public void AddTxFilter(ITxFilter txFilter) => inner.AddTxFilter(txFilter);

    public bool Execute(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec)
    {
        if (currentSpec is IXdcReleaseSpec xdcSpec && tx.IsSpecialTransaction(xdcSpec))
            return true;

        return inner.Execute(tx, parentHeader, currentSpec);
    }
}
