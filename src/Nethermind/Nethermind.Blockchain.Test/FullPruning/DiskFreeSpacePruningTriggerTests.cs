// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core.Timers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class DiskFreeSpacePruningTriggerTests
    {
        [Timeout(Timeout.MaxTestTime)]
        [TestCase(999, ExpectedResult = true)]
        [TestCase(1000, ExpectedResult = false)]
        public bool triggers_on_low_free_space(int availableFreeSpace)
        {
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);

            string path = "path";
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            fileSystem.Path.GetFullPath(path).Returns(path);
            fileSystem.Path.GetPathRoot(path).Returns(path);
            fileSystem.DriveInfo.New(path).AvailableFreeSpace.Returns(availableFreeSpace);

            bool triggered = false;

            DiskFreeSpacePruningTrigger trigger = new(path, 1000, timerFactory, fileSystem);
            trigger.Prune += (o, e) => triggered = true;

            timer.Elapsed += Raise.Event();
            return triggered;
        }
    }
}
