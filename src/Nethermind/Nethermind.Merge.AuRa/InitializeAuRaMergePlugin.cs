// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin;

namespace Nethermind.Merge.AuRa;

[RunnerStepDependencies(dependencies: [typeof(InitializeMergePlugin)], dependents: [typeof(InitializeBlockchain)])]
public class InitializeAuRaMergePlugin(IPoSSwitcher poSSwitcher) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        TxAuRaFilterBuilders.CreateFilter = (originalFilter, fallbackFilter) =>
            originalFilter is MinGasPriceContractTxFilter ? originalFilter
            : new AuRaMergeTxFilter(poSSwitcher, originalFilter, fallbackFilter);

        return Task.CompletedTask;
    }
}
