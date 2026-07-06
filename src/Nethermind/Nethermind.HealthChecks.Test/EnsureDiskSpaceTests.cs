// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test;

public class EnsureDiskSpaceTests
{
    private static readonly long _freeSpaceBytes = (long)(1.GiB * 1.2);

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
        FreeDiskSpaceChecker freeDiskSpaceChecker = new(
            hcConfig,
            GetDriveInfos(1.5f), // below the required threshold
            TimerFactory.Default,
            exitSource,
            LimboLogs.Instance);
        EnsureDiskSpace step = new(hcConfig, freeDiskSpaceChecker, TimerFactory.Default);

        await step.Execute(CancellationToken.None);

        exitSource.Received(exitExpected ? 1 : 0).Exit(ExitCodes.LowDiskSpace);
    }

    private static IDriveInfo[] GetDriveInfos(float availableDiskSpacePercent)
    {
        IDriveInfo drive = Substitute.For<IDriveInfo>();
        drive.AvailableFreeSpace.Returns(_freeSpaceBytes);
        drive.TotalSize.Returns((long)(_freeSpaceBytes * 100.0 / availableDiskSpacePercent));
        drive.RootDirectory.FullName.Returns("C:/");

        return new[] { drive };
    }
}
