// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Transactions;
using Nethermind.Merge.AuRa;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Skips wiring the branch processor on post-merge heads to avoid the multi-million-header
/// startup walk in <see cref="Nethermind.Consensus.AuRa.AuRaBlockFinalizationManager"/>. Pre-merge
/// heads (archive sync from genesis) still wire so validator-set transitions fire.
/// </summary>
public class InitializeBlockchainAuRaMerge(AuRaNethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider, ITxGossipPolicy txGossipPolicy)
    : InitializeBlockchainAuRa(api, chainHeadInfoProvider, txGossipPolicy)
{
    protected override void WireFinalizationBranchProcessor()
    {
        if (!Api.Context.Resolve<IPoSSwitcher>().IsHeadPostMerge(Api.BlockTree!))
            base.WireFinalizationBranchProcessor();
    }
}
