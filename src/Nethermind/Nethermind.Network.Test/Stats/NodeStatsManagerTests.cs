// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
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

            NodeStatsManager manager = new(timerFactory, LimboLogs.Instance, 3);
            Node[] nodes = TestItem.PublicKeys.Take(3).Select(k => new Node(k, new IPEndPoint(IPAddress.Loopback, 30303))).ToArray();
            manager.ReportSyncEvent(nodes[0], NodeStatsEventType.SyncStarted);
            manager.ReportSyncEvent(nodes[1], NodeStatsEventType.SyncStarted);
            manager.ReportSyncEvent(nodes[2], NodeStatsEventType.SyncStarted);
            Node removedNode = new(TestItem.PublicKeyD, IPEndPoint.Parse("192.168.0.4:30303"));
            manager.ReportHandshakeEvent(removedNode, ConnectionDirection.In);
            Assert.That(manager.GetCurrentReputation(removedNode), Is.Not.EqualTo(0));

            timer.Elapsed += Raise.Event();

            Assert.That(manager.GetCurrentReputation(removedNode), Is.EqualTo(0));
            Assert.That(nodes.Select(n => manager.GetCurrentReputation(n)), Does.Not.Contain(0));
        }
    }
}
