// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Db;

namespace Nethermind.Init.Steps;

/// <summary>
/// Opens the state DB during init, so dropping the patricia trie happens here and not mid-processing.
/// </summary>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class DropPruningTrieState(ILifetimeScope rootScope) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        // Resolving it is the work: PruningTrieStoreModule wipes the store as it opens it.
        rootScope.ResolveKeyed<IDb>(DbNames.State);
        return Task.CompletedTask;
    }
}
