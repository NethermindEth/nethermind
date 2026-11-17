// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using ITimer = Nethermind.Core.Timers.ITimer;

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
            TimerFactory.Default,
            exitSource,
            LimboLogs.Instance);

        freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(TimerFactory.Default);
        exitSource.Received(exitExpected ? 1 : 0).Exit(ExitCodes.LowDiskSpace);
    }

    [Test]
    [TestCase(1.5f, true)] //wait until more space is free
    [TestCase(2.5f, false)] //no wait
    public void free_disk_check_ensure_free_on_startup_wait_until_enough(float availableDiskSpacePercent, bool awaitsForFreeSpace)
    {
        TimeSpan deadlockGuard = TimeSpan.FromSeconds(30);
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
            Substitute.For<ITimerFactory>(),
            Substitute.For<IProcessExitSource>(),
            LimboLogs.Instance);
        using ManualResetEventSlim timerStarted = new();
        ITimer startupTimer = Substitute.For<ITimer>();
        startupTimer.When(static timer => timer.Start()).Do(_ => timerStarted.Set());
        ITimerFactory startupTimerFactory = Substitute.For<ITimerFactory>();
        startupTimerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(startupTimer);

        Task t = Task.Run(() => freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(startupTimerFactory));
        if (awaitsForFreeSpace)
        {
            Assert.That(timerStarted.Wait(deadlockGuard) && !t.IsCompleted, Is.True, "the startup check waits on its timer");
            startupTimer.Elapsed += Raise.Event();
        }

        Assert.That(t.Wait(deadlockGuard), Is.True);

        using (Assert.EnterMultipleScope())
        {
            _ = drives[0].Received(awaitsForFreeSpace ? 3 : 1).AvailableFreeSpace;
            Assert.That(timerStarted.IsSet, Is.EqualTo(awaitsForFreeSpace));
            startupTimer.Received(awaitsForFreeSpace ? 1 : 0).Dispose();
        }
    }
}
