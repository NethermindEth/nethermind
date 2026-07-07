// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core.Timers;
using Nethermind.Init.Steps;

namespace Nethermind.HealthChecks;

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

        return Task.CompletedTask;
    }
}
