// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SessionMonitorTests
    {
        private IPingSender _pingSender;

        private IPingSender _noPong;

        [SetUp]
        public void SetUp()
        {
            _pingSender = Substitute.For<IPingSender>();
            _pingSender.SendPing().Returns(Task.FromResult(true));

            _noPong = Substitute.For<IPingSender>();
            _noPong.SendPing().Returns(Task.FromResult(false));
        }

        [Test]
        public void Can_remove_session()
        {
            ISession session = CreateSession();
            SessionMonitor sessionMonitor = new(new NetworkConfig(), LimboLogs.Instance);
            sessionMonitor.AddSession(session);
            sessionMonitor.RemoveSession(session);

            Assert.That(sessionMonitor.Sessions, Is.Empty);
        }

        [Test]
        [Explicit("Travis fails here")]
        public async Task Will_keep_pinging()
        {
            ISession session1 = CreateSession();
            ISession session2 = CreateUnresponsiveSession();

            NetworkConfig networkConfig = new();
            networkConfig.P2PPingInterval = 50;
            SessionMonitor sessionMonitor = new(networkConfig, LimboLogs.Instance);
            sessionMonitor.AddSession(session1);
            sessionMonitor.AddSession(session2);
            sessionMonitor.Start();
            await Task.Delay(300);
            sessionMonitor.Stop();

            await _pingSender.Received().SendPing();
            await _noPong.Received().SendPing();

            Assert.That(session1.State, Is.EqualTo(SessionState.Initialized));
            Assert.That(session2.State, Is.EqualTo(SessionState.Disconnected));
        }

        [Test]
        public void Disconnects_a_session_that_misses_every_pong_and_reports_a_timeout()
        {
            ISession responsive = CreateSession();
            ISession unresponsive = CreateUnresponsiveSession();
            DisconnectReason? reason = null;
            unresponsive.Disconnected += (_, args) => reason = args.DisconnectReason;

            NetworkConfig networkConfig = new();
            networkConfig.P2PPingInterval = 20;
            SessionMonitor sessionMonitor = new(networkConfig, LimboLogs.Instance);
            sessionMonitor.AddSession(responsive);
            sessionMonitor.AddSession(unresponsive);
            sessionMonitor.Start();
            try
            {
                Assert.That(() => unresponsive.IsClosing, Is.True.After(10_000, 20));
                Assert.That(() => reason, Is.EqualTo(DisconnectReason.ReceiveMessageTimeout).After(5_000, 20),
                    "the reason selects the reconnect delay and lets the privileged-node gate reap dead static sessions");
                Assert.That(responsive.IsClosing, Is.False);
            }
            finally
            {
                sessionMonitor.Stop();
            }
        }

        [Test]
        public void Keeps_a_session_that_recovers_between_missed_pongs()
        {
            int calls = 0;
            IPingSender flaky = Substitute.For<IPingSender>();
            flaky.SendPing().Returns(_ => Task.FromResult(Interlocked.Increment(ref calls) % 2 == 0));
            ISession session = CreateSession(flaky);

            NetworkConfig networkConfig = new();
            networkConfig.P2PPingInterval = 300;
            SessionMonitor sessionMonitor = new(networkConfig, LimboLogs.Instance);
            sessionMonitor.AddSession(session);
            sessionMonitor.Start();
            try
            {
                Assert.That(() => session.IsClosing, Is.False.After(2_000),
                    "two missed pongs followed by a pong is a slow peer, not a dead one");
            }
            finally
            {
                sessionMonitor.Stop();
            }
        }

        private ISession CreateSession() => CreateSession(_pingSender);

        private ISession CreateSession(IPingSender pingSender)
        {
            ISession session = new Session(30312, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.PingSender = pingSender;
            session.Handshake(TestItem.PublicKeyB);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
            return session;
        }

        [Test]
        public void AddSession_Staggers_Ping_Times()
        {
            NetworkConfig networkConfig = new() { P2PPingInterval = 10_000 };
            SessionMonitor sessionMonitor = new(networkConfig, LimboLogs.Instance);

            const int sessionCount = 20;
            ISession[] sessions = new ISession[sessionCount];
            for (int i = 0; i < sessionCount; i++)
            {
                sessions[i] = CreateSession();
                sessionMonitor.AddSession(sessions[i]);
            }

            // Sessions should have different LastPingUtc values due to jitter
            DateTime[] pingTimes = sessions.Select(s => s.LastPingUtc).ToArray();
            int distinctCount = pingTimes.Distinct().Count();
            Assert.That(distinctCount, Is.GreaterThan(1), "Sessions added at the same time should have staggered ping times");

            // The spread should cover a meaningful portion of the interval
            TimeSpan spread = pingTimes.Max() - pingTimes.Min();
            Assert.That(spread, Is.GreaterThan(TimeSpan.FromMilliseconds(100)), "Ping time spread should be non-trivial");
        }

        private ISession CreateUnresponsiveSession()
        {
            ISession session = new Session(30312, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.RemoteHost = "1.2.3.4";
            session.RemotePort = 12345;
            session.PingSender = _noPong;
            session.Handshake(TestItem.PublicKeyB);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
            return session;
        }
    }
}
