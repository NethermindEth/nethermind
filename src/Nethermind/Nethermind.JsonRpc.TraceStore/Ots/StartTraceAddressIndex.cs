// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.JsonRpc.TraceStore.Ots;

[RunnerStepDependencies(typeof(StartBlockProcessor))]
internal sealed class StartTraceAddressIndex(TraceAddressIndex index) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index.Start();
        return Task.CompletedTask;
    }
}
