/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */


using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
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

            TestRandom testRandom = new TestRandom((i) => 1);
            SessionMonitor sessionMonitor = new SessionMonitor(new NetworkConfig(), LimboLogs.Instance);
            sessionMonitor.AddSession(session);
            session.Disconnect(DisconnectReason.Other, DisconnectType.Remote, "test");
        }
        
        [TestCase(0)]
        [TestCase(1)]
        [Explicit("Travis fails here")]
        public void Will_keep_pinging(int randomResult)
        {
            ISession session1 = CreateSession();
            ISession session2 = CreateUnresponsiveSession();

            NetworkConfig networkConfig = new NetworkConfig();
            networkConfig.P2PPingInterval = 50;
            TestRandom testRandom = new TestRandom((i) => randomResult);
            SessionMonitor sessionMonitor = new SessionMonitor(networkConfig, LimboLogs.Instance);
            sessionMonitor.AddSession(session1);
            sessionMonitor.AddSession(session2);
            sessionMonitor.Start();
            Thread.Sleep(300);
            sessionMonitor.Stop();

            _pingSender.Received().SendPing();
            _noPong.Received().SendPing();
            
            Assert.AreEqual(SessionState.Initialized, session1.State);
            Assert.AreEqual(randomResult == 0? SessionState.Disconnected : SessionState.Initialized, session2.State);
        }

        private ISession CreateSession()
        {
            ISession session = new Session(30312,  LimboLogs.Instance, Substitute.For<IChannel>());
            session.PingSender = _pingSender;
            session.Handshake(TestItem.PublicKeyB);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
            return session;
        }
        
        private ISession CreateUnresponsiveSession()
        {
            ISession session = new Session(30312, LimboLogs.Instance, Substitute.For<IChannel>());
            session.PingSender = _noPong;
            session.Handshake(TestItem.PublicKeyB);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
            return session;
        }
    }
}