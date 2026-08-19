// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init.Steps;

/// <summary>
/// Runs the one-time Rocks→Arena flat base store conversion (<see cref="FlatBaseStoreConverter"/>) after
/// the databases are open and strictly before block processing and RPC come up, so the migration never
/// overlaps live processing. Registered only when <c>FlatDb.BaseStore=Arena</c> and
/// <c>FlatDb.ConvertBaseStore=true</c>; a boot on an already-converted DB is a no-op.
/// </summary>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ConvertFlatBaseStore(FlatBaseStoreConverter converter) : IStep
{
    public Task Execute(CancellationToken cancellationToken) =>
        Task.Run(() => converter.Convert(cancellationToken), cancellationToken);
}
