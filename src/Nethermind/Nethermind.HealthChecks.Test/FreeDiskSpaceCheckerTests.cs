// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Extensions;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;

namespace Nethermind.HealthChecks.Test
{
    public class FreeDiskSpaceCheckerTests
    {
        private static readonly long _freeSpaceBytes = (long)(1.GiB() * 1.2);

        [Test]
        [TestCase(1.5f, true)] //throw exception - min required 2.5% / available 1.5%
        [TestCase(2.5f, false)]
        public void free_disk_check_ensure_free_on_startup_no_wait(float availableDiskSpacePercent, bool exceptionExpected)
        {
            HealthChecksConfig hcConfig = new()
            {
                LowStorageCheckAwaitOnStartup = false,
                LowStorageSpaceShutdownThreshold = 1,
                LowStorageSpaceWarningThreshold = 5
            };
            FreeDiskSpaceChecker freeDiskSpaceChecker = new(hcConfig, LimboTraceLogger.Instance, GetDriveInfos(availableDiskSpacePercent), Core.Timers.TimerFactory.Default);

            if (exceptionExpected)
                Assert.Throws<NotEnoughDiskSpaceException>(() => freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(Core.Timers.TimerFactory.Default));
            else
                freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(Core.Timers.TimerFactory.Default);
        }

        [Test]
        [TestCase(1.5f, true)] //wait until more space is free
        [TestCase(2.5f, false)] //no wait
        public void free_disk_check_ensure_free_on_startup_wait_until_enough(float availableDiskSpacePercent, bool awaitsForFreeSpace)
        {
            TimeSpan ts = TimeSpan.FromMinutes(1 / 120.0);
            HealthChecksConfig hcConfig = new()
            {
                LowStorageCheckAwaitOnStartup = true,
                LowStorageSpaceShutdownThreshold = 1,
                LowStorageSpaceWarningThreshold = 5
            };
            var drives = GetDriveInfos(availableDiskSpacePercent);
            FreeDiskSpaceChecker freeDiskSpaceChecker = new(hcConfig, LimboTraceLogger.Instance, drives, Core.Timers.TimerFactory.Default, ts.TotalMinutes);

            Stopwatch sw = new();
            Task t = new(() =>
            {
                sw.Start();
                freeDiskSpaceChecker.EnsureEnoughFreeSpaceOnStart(Core.Timers.TimerFactory.Default);
                sw.Stop();
            });
            t.Start();
            Thread.Sleep(10);
            drives[0].AvailableFreeSpace.Returns(_freeSpaceBytes * 3);
            bool completed = t.Wait((int)ts.TotalMilliseconds * 2);

            Assert.IsTrue(completed);
            if (awaitsForFreeSpace)
                Assert.IsTrue(sw.Elapsed.TotalMilliseconds >= ts.TotalMilliseconds);
            else
                Assert.IsTrue(sw.Elapsed.TotalMilliseconds < ts.TotalMilliseconds);
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
}
