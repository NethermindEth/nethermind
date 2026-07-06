// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;

namespace Nethermind.HealthChecks;

/// <summary>
/// Starts the health-check background services: the periodic free-disk-space monitor and, on merge
/// networks, the consensus-client liveness tracker.
/// </summary>
/// <remarks>
/// Depends on <see cref="RegisterRpcModules"/> so it runs after <c>InitializePlugins</c> has completed
/// (transitively). That ordering is required: <see cref="HealthChecksPlugin.Init"/> performs the
/// blocking startup disk guard, and the periodic <see cref="FreeDiskSpaceChecker"/> timer must not
/// start — and potentially exit the process — until that guard has finished.
/// </remarks>
[RunnerStepDependencies(typeof(RegisterRpcModules))]
public class StartHealthChecks(
    IHealthChecksConfig healthChecksConfig,
    IMergeConfig mergeConfig,
    FreeDiskSpaceChecker freeDiskSpaceChecker,
    Lazy<IEngineRequestsTracker> engineRequestsTracker,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<StartHealthChecks>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (healthChecksConfig.LowStorageSpaceWarningThreshold > 0 || healthChecksConfig.LowStorageSpaceShutdownThreshold > 0)
        {
            try
            {
                freeDiskSpaceChecker.StartAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Failed to initialize available disk space check module", ex);
            }
        }

        if (mergeConfig.Enabled)
        {
            _ = engineRequestsTracker.Value.StartAsync(); // Fire and forget
        }

        return Task.CompletedTask;
    }
}
