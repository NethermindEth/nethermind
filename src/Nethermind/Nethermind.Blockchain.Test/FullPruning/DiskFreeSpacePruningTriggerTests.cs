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
using MathGmp.Native;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core.Timers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class DiskFreeSpacePruningTriggerTests
    {
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
            fileSystem.DriveInfo.FromDriveName(path).AvailableFreeSpace.Returns(availableFreeSpace);

            bool triggered = false;
            
            DiskFreeSpacePruningTrigger trigger = new(path, 1000, timerFactory, fileSystem);
            trigger.Prune += (o, e) => triggered = true;
            
            timer.Elapsed += Raise.Event();
            return triggered;
        }
    }
}
