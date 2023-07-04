// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
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
            Node removedNode = new(TestItem.PublicKeyD, IPEndPoint.Parse("192.168.0.4:30303"));
            manager.ReportHandshakeEvent(removedNode, ConnectionDirection.In);
            manager.GetCurrentReputation(removedNode).Should().NotBe(0);

            timer.Elapsed += Raise.Event();

            manager.GetCurrentReputation(removedNode).Should().Be(0);
            nodes.Select(n => manager.GetCurrentReputation(n)).Should().NotContain(0);
        }
    }
}
