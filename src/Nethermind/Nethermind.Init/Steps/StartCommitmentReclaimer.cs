// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(dependencies: [typeof(InitializeBlockTree)])]
public class StartCommitmentReclaimer(CommitmentReclaimer reclaimer) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        reclaimer.Start();
        return Task.CompletedTask;
    }
}
