// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
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
    protected override async Task InitBlockchain()
    {
        await base.InitBlockchain();

        // Construct the terminal-block disposer now (after WireFinalizationBranchProcessor ran in base):
        // it either disposes the finalization manager immediately (head already post-merge) or subscribes
        // to IPoSSwitcher.TerminalBlockReached. Resolving here triggers that constructor side-effect.
        // Safe at this step — the head is loaded, IPoSSwitcher is initialized, and the block processor has
        // not started, so the once-only TerminalBlockReached signal cannot be missed. Autofac owns its
        // lifetime and disposal.
        Api.Context.Resolve<AuRaTerminalBlockDisposer>();
    }

    protected override void WireFinalizationBranchProcessor()
    {
        if (!Api.Context.Resolve<IPoSSwitcher>().IsHeadPostMerge(Api.BlockTree!))
            base.WireFinalizationBranchProcessor();
    }
}
