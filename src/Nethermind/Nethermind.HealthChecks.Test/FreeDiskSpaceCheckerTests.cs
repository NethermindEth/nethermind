// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using System.Threading.Tasks;
using Nethermind.Config;

namespace Nethermind.HealthChecks.Test;

public class FreeDiskSpaceCheckerTests
{
    [Test]
    [TestCase(1.5f, true)] //throw exception - min required 2.5% / available 1.5%
    [TestCase(2.5f, false)]
    public void free_disk_check_ensure_free_on_startup_no_wait(float availableDiskSpacePercent, bool exitExpected)
    {
        HealthChecksConfig hcConfig = new()
        {
            LowStorageCheckAwaitOnStartup = false,
            LowStorageSpaceShutdownThreshold = 1,
            LowStorageSpaceWarningThreshold = 5
        };
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        FreeDiskSpaceChecker freeDiskSpaceChecker = new(
            hcConfig,
            DiskSpaceTestHelper.GetDriveInfos(availableDiskSpacePercent),
            Core.Timers.TimerFactory.Default,
            exitSource,
            LimboLogs.Instance);

        freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(Core.Timers.TimerFactory.Default);
        exitSource.Received(exitExpected ? 1 : 0).Exit(ExitCodes.LowDiskSpace);
    }

    [Test]
    [TestCase(1.5f, true)] //wait until more space is free
    [TestCase(2.5f, false)] //no wait
    public void free_disk_check_ensure_free_on_startup_wait_until_enough(float availableDiskSpacePercent, bool awaitsForFreeSpace)
    {
        TimeSpan ts = TimeSpan.FromMilliseconds(100);
        HealthChecksConfig hcConfig = new()
        {
            LowStorageCheckAwaitOnStartup = true,
            LowStorageSpaceShutdownThreshold = 1,
            LowStorageSpaceWarningThreshold = 5
        };
        IDriveInfo[] drives = DiskSpaceTestHelper.GetDriveInfos(availableDiskSpacePercent);
        drives[0].AvailableFreeSpace.Returns(a => DiskSpaceTestHelper.FreeSpaceBytes,
                                                a => 3 * DiskSpaceTestHelper.FreeSpaceBytes);
        FreeDiskSpaceChecker freeDiskSpaceChecker = new(
            hcConfig,
            drives,
            Core.Timers.TimerFactory.Default,
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance,
            ts.TotalMinutes);

        Task t = Task.Run(() => freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(Core.Timers.TimerFactory.Default));
        bool completed = t.Wait((int)ts.TotalMilliseconds * 2);

        Assert.That(completed, Is.True);

        _ = drives[0].Received(awaitsForFreeSpace ? 3 : 1).AvailableFreeSpace;
    }
}
