// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa;

/// <summary>
/// Merge-aware variant of <see cref="InitializeBlockchainAuRa"/>. Skips wiring the branch processor
/// into the AuRa finalization manager on post-merge chains — AuRa finalization is no longer active
/// there, and otherwise the startup catch-up walk in <c>AuRaBlockFinalizationManager.Initialize</c>
/// allocates millions of BlockHeaders on long post-merge chains like Gnosis.
/// </summary>
public class InitializeBlockchainAuRaMerge(AuRaNethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider, ITxGossipPolicy txGossipPolicy)
    : InitializeBlockchainAuRa(api, chainHeadInfoProvider, txGossipPolicy)
{
    protected override void WireFinalizationBranchProcessor()
    {
        if (!Api.Context.Resolve<IPoSSwitcher>().HasEverReachedTerminalBlock())
            base.WireFinalizationBranchProcessor();
    }
}
