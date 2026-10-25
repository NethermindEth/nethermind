// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Init.Steps;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class InitializeBlockchainAuRa(AuRaNethermindApi api) : InitializeBlockchain(api)
{
    protected AuRaNethermindApi Api => api;

    protected override async Task InitBlockchain()
    {
        await base.InitBlockchain();

        WireFinalizationBranchProcessor();
    }

    /// <summary>
    /// Wires the branch processor into the finalization manager. Override in the merge variant
    /// to skip wiring on post-merge chains, where AuRa finalization is no longer active and the
    /// startup catch-up walk would allocate millions of BlockHeaders on long chains like Gnosis.
    /// </summary>
    /// <remarks>
    /// Got cyclic dependency. AuRaBlockFinalizationManager -> IAuraValidator -> AuraBlockProcessor -> AuraBlockFinalizationManager.
    /// </remarks>
    protected virtual void WireFinalizationBranchProcessor() =>
        api.Context.Resolve<IAuRaBlockFinalizationManager>().SetMainBlockBranchProcessor(api.MainProcessingContext!.BranchProcessor!);
}
