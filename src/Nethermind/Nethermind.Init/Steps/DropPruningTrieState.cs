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
/// Ordered after <see cref="ImportFlatDb"/> so the drop can never run while the import is still
/// reading the trie. The gate in PruningTrieStoreModule already ensures that by requiring a populated
/// flat store, which the import only produces at its end; the edge keeps it from resting on the gate alone.
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
