// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Analyzers;
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
            RlpxHost host = new(
                Substitute.For<IMessageSerializationService>(),
                TestItem.PublicKeyA, GegAvailableLocalPort(), null,
                Substitute.For<IHandshakeService>(),
                Substitute.For<ISessionMonitor>(),
                NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance,
                TimeSpan.Zero);
            await host.Init();
            await host.Shutdown();
        }

        private static int GegAvailableLocalPort()
        {
            TcpListener l = new(IPAddress.Loopback, 0);
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
