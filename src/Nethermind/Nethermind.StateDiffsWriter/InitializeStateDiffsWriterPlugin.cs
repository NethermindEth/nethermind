// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.StateDiffsWriter.Service;

namespace Nethermind.StateDiffsWriter;

/// <summary>
/// Starts the <see cref="DiffsPruner"/> background loop. Injecting it also constructs its
/// <see cref="DiffsWriterService"/> dependency, attaching that service's <c>NewHeadBlock</c> subscription.
/// </summary>
/// <remarks>
/// <see cref="LoadGenesisBlock"/> is declared as a dependent so the subscription is live before the genesis
/// head is raised. Container shutdown disposes both singletons, so no explicit teardown is needed.
/// </remarks>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockTree)], dependents: [typeof(LoadGenesisBlock)])]
public class InitializeStateDiffsWriterPlugin(DiffsPruner pruner) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        pruner.Start();
        return Task.CompletedTask;
    }
}
