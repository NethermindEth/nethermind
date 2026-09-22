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
/// <remarks>
/// The gate in PruningTrieStoreModule is what protects a running import: it refuses while the flat store is
/// empty, and a populated flat store means <see cref="ImportFlatDb"/> has already finished (its Execute
/// returns early). The dependency on ImportFlatDb only documents the intended order; the drop happens when the
/// state DB is first opened, and ImportFlatDb's own constructor dependencies open it before either step runs.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree), typeof(ImportFlatDb)],
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
