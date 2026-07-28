// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class RlpxHostIntegrationTests
{
    [TestCase(true, false, null, "203.0.113.1", "203.0.113.1", Description = "Exact match config: repeat IP still accepted")]
    [TestCase(true, true, null, "203.0.113.1", "203.0.113.50", Description = "Subnet bucketing config: repeat subnet still accepted")]
    [TestCase(false, false, null, "203.0.113.1", "203.0.113.1", Description = "Filtering disabled: still accepted")]
    public async Task ShouldContact_IsNeverRateLimitedByTheRecentIpFilter(bool filterEnabled, bool subnetBucketing, string? externalIp,
        string addr1, string addr2)
    {
        RlpxHost host = CreateHost(filterEnabled, subnetBucketing, externalIp);
        try
        {
            Assert.That(host.ShouldContact(IPAddress.Parse(addr1)), Is.True, "first attempt accepted");
            Assert.That(host.ShouldContact(IPAddress.Parse(addr2)), Is.True,
                "a repeat contact attempt must not be rejected: the recent-IP filter only gates inbound connections");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task TrackSessionActivity_DoesNotAffectShouldContact()
    {
        // Regression guard: TrackSessionActivity feeds the same NodeFilter that ShouldRejectInbound consults for
        // inbound connections. ShouldContact must stay independent of it, otherwise contacting (or receiving
        // traffic from) a peer ourselves could make us reject that peer's own genuine inbound connection shortly after.
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: true);
        try
        {
            IPAddress receivedIp = IPAddress.Parse("203.0.113.1");
            ISession receivedSession = Substitute.For<ISession>();
            receivedSession.Node.Returns(new Node(TestItem.PublicKeyA, receivedIp.ToString(), 30303));

            host.TrackSessionActivity(receivedSession);
            receivedSession.MsgReceived += Raise.EventWith(receivedSession, new PeerEventArgs(receivedSession.Node, "eth", 1, 32));

            Assert.That(host.ShouldContact(receivedIp), Is.True, "received traffic must not block our own future outbound attempts");

            IPAddress deliveredIp = IPAddress.Parse("198.51.100.1");
            ISession deliveredSession = Substitute.For<ISession>();
            deliveredSession.Node.Returns(new Node(TestItem.PublicKeyA, deliveredIp.ToString(), 30303));

            host.TrackSessionActivity(deliveredSession);
            deliveredSession.MsgDelivered += Raise.EventWith(deliveredSession, new PeerEventArgs(deliveredSession.Node, "eth", 2, 64));

            Assert.That(host.ShouldContact(deliveredIp), Is.True, "sent traffic must not block our own future outbound attempts");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    private static RlpxHost CreateHost(bool filterEnabled, bool subnetBucketing, string? externalIp = null)
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = GetAvailablePort(),
            FilterPeersByRecentIp = filterEnabled,
            FilterPeersBySameSubnet = subnetBucketing,
            ExternalIp = externalIp,
            MaxActivePeers = 50
        };

        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Loopback, externalIp is null ? IPAddress.None : IPAddress.Parse(externalIp))));

        return new RlpxHost(
            Substitute.For<IMessageSerializationService>(),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            ipResolver,
            Substitute.For<IPrivilegedIpProvider>(),
            LimboLogs.Instance);
    }

    private static int GetAvailablePort()
    {
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
