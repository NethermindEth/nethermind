// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.Merge.Plugin;

namespace Nethermind.HealthChecks;

/// <summary>
/// Starts the consensus-client liveness tracker on merge networks.
/// </summary>
/// <remarks>
/// Depends on <see cref="RegisterRpcModules"/> so it runs after plugin RPC modules have been registered.
/// The periodic free-disk-space monitor is started earlier, by <see cref="EnsureDiskSpace"/>.
/// </remarks>
[RunnerStepDependencies(typeof(RegisterRpcModules))]
public class StartHealthChecks(
    IMergeConfig mergeConfig,
    Lazy<IEngineRequestsTracker> engineRequestsTracker) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        if (mergeConfig.Enabled)
        {
            _ = engineRequestsTracker.Value.StartAsync(); // Fire and forget
        }

        return Task.CompletedTask;
    }
}
