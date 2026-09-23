// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Db;

namespace Nethermind.Init.Steps;

/// <summary>
/// Opens the state DB during init, so dropping the patricia trie happens here and not mid-processing.
/// </summary>
/// <remarks>
/// Nothing orders this after <see cref="ImportFlatDb"/>, and nothing needs to: the drop happens when the state DB
/// is first opened, which ImportFlatDb's own constructor dependencies do before either step runs, and the gate in
/// PruningTrieStoreModule is what protects a running import. It refuses while the flat store is empty, and a
/// populated flat store means the import has already finished. Resolving the same singleton concurrently with
/// ImportFlatDb is harmless.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class DropPruningTrieState([KeyFilter(DbNames.State)] Lazy<IDb> stateDb) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        // Resolving it is the work: PruningTrieStoreModule wipes the store as it opens it.
        _ = stateDb.Value;
        return Task.CompletedTask;
    }
}
