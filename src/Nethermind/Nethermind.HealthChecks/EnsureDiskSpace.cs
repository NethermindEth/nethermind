// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core.Timers;
using Nethermind.Init.Steps;

namespace Nethermind.HealthChecks;

/// <summary>
/// Runs the blocking startup disk-space guard and then starts the periodic <see cref="FreeDiskSpaceChecker"/> monitor.
/// </summary>
/// <remarks>
/// The periodic monitor is started here rather than in a later step so that it is already running while
/// <see cref="ReviewBlockTree"/> processes blocks that were downloaded but not processed before a restart.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)])]
public class EnsureDiskSpace(
    IHealthChecksConfig healthChecksConfig,
    FreeDiskSpaceChecker freeDiskSpaceChecker,
    ITimerFactory timerFactory) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        if (healthChecksConfig.LowStorageSpaceShutdownThreshold > 0)
        {
            freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(timerFactory);
        }

        if (healthChecksConfig.LowStorageSpaceWarningThreshold > 0 || healthChecksConfig.LowStorageSpaceShutdownThreshold > 0)
        {
            freeDiskSpaceChecker.StartAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }
}
