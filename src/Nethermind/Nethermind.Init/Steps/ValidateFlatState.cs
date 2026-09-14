// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(ApplyMemoryHint))]
public sealed class ValidateFlatState(FlatStateActivationPolicy policy) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = policy;
        return Task.CompletedTask;
    }
}
