// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Merge-aware variant of <see cref="InitializeBlockchainAuRa"/>. Skips wiring the branch processor
/// into the AuRa finalization manager only when the current chain head is already post-merge.
/// In that state AuRa finalization is no longer active, and the startup catch-up walk in
/// <c>AuRaBlockFinalizationManager.Initialize</c> would allocate millions of BlockHeaders on long
/// post-merge chains like Gnosis. For pre-merge heads (fresh archive sync from genesis, or a
/// restart mid-sync before the merge) the wiring must still run so AuRa finalization events fire
/// and validator-set transitions (e.g. Gnosis block 1300) are applied.
/// </summary>
public class InitializeBlockchainAuRaMerge(AuRaNethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider, ITxGossipPolicy txGossipPolicy)
    : InitializeBlockchainAuRa(api, chainHeadInfoProvider, txGossipPolicy)
{
    protected override void WireFinalizationBranchProcessor()
    {
        // IPoSSwitcher.HasEverReachedTerminalBlock() is not a safe guard here: on a fresh archive
        // DB it already returns true as soon as Merge.FinalTotalDifficulty is set in config (see
        // PoSSwitcher.Initialize), even though the head is still at genesis. Inspect the actual
        // head instead so pre-merge archive sync still wires AuRa finalization correctly.
        BlockHeader? head = Api.BlockTree?.Head?.Header;
        if (head is null || !Api.Context.Resolve<IPoSSwitcher>().IsPostMerge(head))
            base.WireFinalizationBranchProcessor();
    }
}
