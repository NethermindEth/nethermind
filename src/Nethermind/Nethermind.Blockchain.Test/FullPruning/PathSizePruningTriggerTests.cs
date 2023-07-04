// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using FluentAssertions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core.Timers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class PathSizePruningTriggerTests
    {
        [Timeout(Timeout.MaxTestTime)]
        [TestCase(300, ExpectedResult = true)]
        [TestCase(400, ExpectedResult = false)]
        public bool triggers_on_path_too_big(int threshold)
        {
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);

            string path = "path";
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            IFileInfo[] files = new[]
            {
                GetFile(10),
                GetFile(100),
                GetFile(200)
            };
            fileSystem.Directory.Exists(path).Returns(true);
            fileSystem.DirectoryInfo.New(path).EnumerateFiles().Returns(files);

            bool triggered = false;

            PathSizePruningTrigger trigger = new(path, threshold, timerFactory, fileSystem);
            trigger.Prune += (o, e) => triggered = true;

            timer.Elapsed += Raise.Event();
            return triggered;
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void throws_on_nonexisting_path()
        {
            Action action = () => new PathSizePruningTrigger("path", 5, null!, Substitute.For<IFileSystem>());
            action.Should().Throw<ArgumentException>();
        }

        private static IFileInfo GetFile(long length)
        {
            IFileInfo fileInfo = Substitute.For<IFileInfo>();
            fileInfo.Length.Returns(length);
            return fileInfo;
        }
    }
}
