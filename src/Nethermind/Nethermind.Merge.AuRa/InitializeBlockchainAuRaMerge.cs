// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa;

/// <summary>
/// Skips wiring the branch processor on post-merge heads to avoid the multi-million-header
/// startup walk in <see cref="AuRaBlockFinalizationManager"/>. Pre-merge heads (archive sync
/// from genesis) still wire so validator-set transitions fire.
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
