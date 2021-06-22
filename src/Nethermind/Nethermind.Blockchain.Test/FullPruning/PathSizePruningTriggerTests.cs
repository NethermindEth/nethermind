//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            fileSystem.DirectoryInfo.FromDirectoryName(path).EnumerateFiles().Returns(files);

            bool triggered = false;
            
            PathSizePruningTrigger trigger = new(path, threshold, timerFactory, fileSystem);
            trigger.Prune += (o, e) => triggered = true;
            
            timer.Elapsed += Raise.Event();
            return triggered;
        }

        [Test]
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
