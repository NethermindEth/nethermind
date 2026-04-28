// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

internal class XdcTxGossipPolicy(ISpecProvider provider, IChainHeadInfoProvider chainHeadInfoProvider) : ITxGossipPolicy
{
    public bool ShouldGossipTransaction(Transaction tx)
    {
        IXdcReleaseSpec spec = (IXdcReleaseSpec)provider.GetXdcSpec(chainHeadInfoProvider.HeadNumber);

        if (!tx.RequiresSpecialHandling(spec))
            return true;

        if (tx.IsSignTransaction(spec))
            return true;

        return false;
    }
}
