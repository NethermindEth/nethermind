// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.History.Changesets;

namespace Nethermind.Init.Steps;

/// <summary>Starts the per-transaction changeset builder; a no-op when <c>FlatDb.HistoryTransactionIndexEnabled</c>
/// is off.</summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeNetwork)])]
public sealed class StartTransactionChangesetBuilder(TransactionChangesetBuilder builder) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        // The builder is stopped before disposal by the service-stopper middleware, which registers every
        // IStoppableService it activates; registering it here as well would only queue the same instance twice.
        builder.Start();
        return Task.CompletedTask;
    }
}
