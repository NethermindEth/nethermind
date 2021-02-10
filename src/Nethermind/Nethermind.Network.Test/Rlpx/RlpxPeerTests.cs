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

using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class RlpxPeerTests
    {
        [Test]
        public async Task Start_stop()
        {
            RlpxPeer peer = new RlpxPeer(
                Substitute.For<IMessageSerializationService>(),
                TestItem.PublicKeyA, GegAvailableLocalPort(),
                Substitute.For<IHandshakeService>(),
                Substitute.For<ISessionMonitor>(),
                NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);
            await peer.Init();
            await peer.Shutdown();
        }

        private static int GegAvailableLocalPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try
            {
                return ((IPEndPoint)l.LocalEndpoint).Port;
            }
            finally
            {
                l.Stop();
            }
        }
    }
}
