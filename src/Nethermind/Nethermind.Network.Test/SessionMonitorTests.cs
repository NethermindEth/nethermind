// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public void Will_unregister_on_disconnect()
        {
            ISession session = CreateSession();
            SessionMonitor sessionMonitor = new(new NetworkConfig(), LimboLogs.Instance);
            sessionMonitor.AddSession(session);
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
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

        private ISession CreateSession()
        {
            ISession session = new Session(30312, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.PingSender = _pingSender;
            session.Handshake(TestItem.PublicKeyB);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
            return session;
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
