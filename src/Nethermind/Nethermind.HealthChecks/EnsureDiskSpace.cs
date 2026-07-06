// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core.Timers;
using Nethermind.Init.Steps;

namespace Nethermind.HealthChecks;

/// <summary>
/// Runs the blocking startup disk-space guard: if the node is below the required free-space threshold it
/// either blocks until enough space is available (<see cref="IHealthChecksConfig.LowStorageCheckAwaitOnStartup"/>)
/// or exits the process.
/// </summary>
/// <remarks>
/// Depends on <see cref="InitializeBlockTree"/> to run at the same point in the pipeline as the original
/// <see cref="HealthChecksPlugin.Init"/> did — plugin initialization runs inside <see cref="InitializePlugins"/>,
/// which itself depends on <see cref="InitializeBlockTree"/>. This must also complete before
/// <see cref="StartHealthChecks"/> starts the periodic <see cref="FreeDiskSpaceChecker"/> timer, which is why
/// <see cref="StartHealthChecks"/> depends on this step.
/// </remarks>
[RunnerStepDependencies(typeof(InitializeBlockTree))]
public class EnsureDiskSpace(
    IHealthChecksConfig healthChecksConfig,
    FreeDiskSpaceChecker freeDiskSpaceChecker,
    ITimerFactory timerFactory) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        // Will throw an exception and close app or block until enough disk space is available (LowStorageCheckAwaitOnStartup)
        if (healthChecksConfig.LowStorageSpaceShutdownThreshold > 0)
        {
            freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(timerFactory);
        }

        return Task.CompletedTask;
    }
}
