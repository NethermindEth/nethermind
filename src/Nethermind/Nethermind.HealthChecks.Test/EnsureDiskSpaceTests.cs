// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using ITimer = Nethermind.Core.Timers.ITimer;

namespace Nethermind.HealthChecks.Test;

public class EnsureDiskSpaceTests
{
    [TestCase(0, false, TestName = "Shutdown threshold disabled - guard skipped")]
    [TestCase(1, true, TestName = "Shutdown threshold enabled and disk low - process exits")]
    public async Task Execute_runs_startup_guard_only_when_shutdown_threshold_enabled(long shutdownThreshold, bool exitExpected)
    {
        HealthChecksConfig hcConfig = new()
        {
            LowStorageCheckAwaitOnStartup = false,
            LowStorageSpaceShutdownThreshold = shutdownThreshold,
            LowStorageSpaceWarningThreshold = 5
        };
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        await using FreeDiskSpaceChecker freeDiskSpaceChecker = new(
            hcConfig,
            DiskSpaceTestHelper.GetDriveInfos(1.5f), // below the required threshold
            TimerFactory.Default,
            exitSource,
            LimboLogs.Instance);
        EnsureDiskSpace step = new(hcConfig, freeDiskSpaceChecker, TimerFactory.Default);

        await step.Execute(CancellationToken.None);

        exitSource.Received(exitExpected ? 1 : 0).Exit(ExitCodes.LowDiskSpace);
    }

    [TestCase(0, 0, false)]
    [TestCase(5, 0, true)]
    [TestCase(0, 1, true)]
    [TestCase(5, 1, true)]
    public async Task Execute_starts_periodic_disk_space_check_when_any_threshold_enabled(float warningThreshold, float shutdownThreshold, bool startExpected)
    {
        HealthChecksConfig hcConfig = new()
        {
            LowStorageCheckAwaitOnStartup = false,
            LowStorageSpaceShutdownThreshold = shutdownThreshold,
            LowStorageSpaceWarningThreshold = warningThreshold
        };
        ITimer timer = Substitute.For<ITimer>();
        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
        await using FreeDiskSpaceChecker freeDiskSpaceChecker = new(
            hcConfig,
            DiskSpaceTestHelper.GetDriveInfos(50f),
            timerFactory,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);
        EnsureDiskSpace step = new(hcConfig, freeDiskSpaceChecker, TimerFactory.Default);

        await step.Execute(CancellationToken.None);

        timer.Received(startExpected ? 1 : 0).Start();
    }
}
