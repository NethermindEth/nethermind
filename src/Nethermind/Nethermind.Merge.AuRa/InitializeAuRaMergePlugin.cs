// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;

namespace Nethermind.Merge.AuRa;

/// <summary>
/// Runs the shared merge initialization (see <see cref="InitializeMergePlugin.Configure"/>) and then installs
/// the AuRa merge tx-filter hook. It performs the merge init directly rather than depending on
/// <see cref="InitializeMergePlugin"/> because a plugin module may only register steps from its own assembly.
/// </summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockTree)], dependents: [typeof(InitializeBlockchain), typeof(RegisterRpcModules)])]
public class InitializeAuRaMergePlugin(
    IMergeConfig mergeConfig,
    IBlocksConfig blocksConfig,
    ISpecProvider specProvider,
    IJsonRpcConfig jsonRpcConfig,
    ILogManager logManager,
    IPoSSwitcher poSSwitcher) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        InitializeMergePlugin.Configure(mergeConfig, blocksConfig, specProvider, jsonRpcConfig, logManager);

        TxAuRaFilterBuilders.CreateFilter = (originalFilter, fallbackFilter) =>
            originalFilter is MinGasPriceContractTxFilter ? originalFilter
            : new AuRaMergeTxFilter(poSSwitcher, originalFilter, fallbackFilter);

        return Task.CompletedTask;
    }
}
