// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

internal class XdcTxGossipPolicy(ISpecProvider specProvider, IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    public bool ShouldGossipTransaction(Transaction tx)
    {
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(chainHeadInfoProvider.HeadNumber);

        if (!tx.RequiresSpecialHandling(spec))
            return true;

        if (tx.IsSignTransaction(spec))
            return true;

        return false;
    }
}
