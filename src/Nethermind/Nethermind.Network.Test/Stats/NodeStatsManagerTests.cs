// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using ITimer = Nethermind.Core.Timers.ITimer;

namespace Nethermind.Network.Test.Stats
{
    public class NodeStatsManagerTests
    {
        [Test]
        public void should_remove_excessive_stats()
        {
            NodeStatsManager manager = CreateManager(LimboLogs.Instance, 3, out ITimer timer);
            Node[] nodes = TestItem.PublicKeys.Take(3).Select(k => new Node(k, new IPEndPoint(IPAddress.Loopback, 30303))).ToArray();
            manager.ReportSyncEvent(nodes[0], NodeStatsEventType.SyncStarted);
            manager.ReportSyncEvent(nodes[1], NodeStatsEventType.SyncStarted);
            manager.ReportSyncEvent(nodes[2], NodeStatsEventType.SyncStarted);
            Node removedNode = new(TestItem.PublicKeyD, IPEndPoint.Parse("192.168.0.4:30303"));
            manager.ReportHandshakeEvent(removedNode, ConnectionDirection.In);
            Assert.That(manager.GetCurrentReputation(removedNode), Is.Not.EqualTo(0));

            timer.Elapsed += Raise.Event();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.GetCurrentReputation(removedNode), Is.EqualTo(0));
                Assert.That(nodes.Select(n => manager.GetCurrentReputation(n)), Does.Not.Contain(0));
            }
        }

        [Test]
        public async Task should_clean_up_without_errors_while_nodes_are_added()
        {
            const int maxCount = 10;
            TimeSpan timeout = TimeSpan.FromSeconds(10);
            TestLogger logger = new() { IsTrace = false, IsDebug = false, IsInfo = false, IsWarn = false };
            NodeStatsManager manager = CreateManager(new OneLoggerLogManager(new ILogger(logger)), maxCount, out ITimer timer);

            using CancellationTokenSource cts = new();
            int added = 0;
            Task writer = Task.Run(() =>
            {
                byte[] key = new byte[PublicKey.LengthInBytes];
                for (int i = 0; !cts.IsCancellationRequested; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(key, i);
                    manager.ReportHandshakeEvent(new Node(new PublicKey(key), new IPEndPoint(IPAddress.Loopback, 30303)), ConnectionDirection.In);
                    Interlocked.Increment(ref added);
                }
            });
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref added) > maxCount * 10, timeout), Is.True);

            int cleanups = 0;
            int addedTarget = Volatile.Read(ref added) + 10_000;
            Stopwatch stopwatch = Stopwatch.StartNew();
            while ((cleanups < 200 || Volatile.Read(ref added) < addedTarget) && stopwatch.Elapsed < timeout)
            {
                timer.Elapsed += Raise.Event();
                cleanups++;
            }

            await cts.CancelAsync();
            await writer;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Volatile.Read(ref added), Is.GreaterThanOrEqualTo(addedTarget));
                Assert.That(logger.LogList, Is.Empty);
            }
            timer.Received(cleanups + 1).Start();
        }

        private static NodeStatsManager CreateManager(ILogManager logManager, int maxCount, out ITimer timer)
        {
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            timer = Substitute.For<ITimer>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(timer);
            return new NodeStatsManager(timerFactory, logManager, maxCount);
        }
    }
}
