//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Timers;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Stats
{
    public class NodeStatsManagerTests
    {
        [Test]
        public void should_remove_excessive_stats()
        {
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            ITimer timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            
            var manager = new NodeStatsManager(timerFactory, LimboLogs.Instance, 3);
            var nodes = TestItem.PublicKeys.Take(3).Select(k => new Node(k, new IPEndPoint(IPAddress.Loopback, 30303))).ToArray();
            manager.ReportSyncEvent(nodes[0], NodeStatsEventType.SyncStarted);
            manager.ReportSyncEvent(nodes[1], NodeStatsEventType.SyncStarted);
            manager.ReportSyncEvent(nodes[2], NodeStatsEventType.SyncStarted);
            Node removedNode = new Node(TestItem.PublicKeyD, IPEndPoint.Parse("192.168.0.4:30303"));
            manager.ReportHandshakeEvent(removedNode, ConnectionDirection.In);
            manager.GetCurrentReputation(removedNode).Should().NotBe(0);

            timer.Elapsed += Raise.Event();

            manager.GetCurrentReputation(removedNode).Should().Be(0);
            nodes.Select(n => manager.GetCurrentReputation(n)).Should().NotContain(0);
        }
    }
}
