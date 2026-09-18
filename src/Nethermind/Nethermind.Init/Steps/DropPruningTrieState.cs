// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Db;

namespace Nethermind.Init.Steps;

/// <summary>
/// Forces the state DB open while the node is still initializing, so that dropping the patricia trie
/// happens here rather than partway through block processing.
/// </summary>
/// <remarks>
/// The drop itself is decided and performed by the state DB registration in
/// <see cref="Modules.PruningTrieStoreModule"/>; this step only moves it earlier. Resolution is otherwise
/// lazy, and on a converted mainnet node the deletion was measured pausing a node that was already
/// following the chain for about 20 seconds. Done here it lands inside the restart the operator already
/// accepted instead of looking like a stall.
/// This step is registered only when the drop is configured, so nothing else changes when it is not.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class DropPruningTrieState(ILifetimeScope rootScope) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        // Resolving it is the work: the registration wipes the store as it opens it.
        rootScope.ResolveKeyed<IDb>(DbNames.State);
        return Task.CompletedTask;
    }
}
