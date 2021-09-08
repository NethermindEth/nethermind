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

using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
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
            SessionMonitor sessionMonitor = new SessionMonitor(new NetworkConfig(), LimboLogs.Instance);
            sessionMonitor.AddSession(session);
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
        }
        
        [Test]
        [Explicit("Travis fails here")]
        public async Task Will_keep_pinging()
        {
            ISession session1 = CreateSession();
            ISession session2 = CreateUnresponsiveSession();

            NetworkConfig networkConfig = new NetworkConfig();
            networkConfig.P2PPingInterval = 50;
            SessionMonitor sessionMonitor = new SessionMonitor(networkConfig, LimboLogs.Instance);
            sessionMonitor.AddSession(session1);
            sessionMonitor.AddSession(session2);
            sessionMonitor.Start();
            await Task.Delay(300);
            sessionMonitor.Stop();

            await _pingSender.Received().SendPing();
            await _noPong.Received().SendPing();
            
            Assert.AreEqual(SessionState.Initialized, session1.State);
            Assert.AreEqual(SessionState.Disconnected, session2.State);
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
