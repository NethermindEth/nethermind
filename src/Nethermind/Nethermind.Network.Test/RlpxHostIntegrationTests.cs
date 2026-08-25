// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
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
    [TestCase(true, false, null, "203.0.113.1", "203.0.113.1", false, Description = "Exact match: blocks same IP")]
    [TestCase(true, false, null, "203.0.113.1", "198.51.100.1", true, Description = "Exact match: allows different IP")]
    [TestCase(true, true, null, "203.0.113.1", "203.0.113.50", false, Description = "Subnet bucketing: blocks same subnet")]
    [TestCase(false, false, null, "203.0.113.1", "203.0.113.1", true, Description = "Filtering disabled: always accepts")]
    [TestCase(true, true, "192.168.1.100", "192.168.1.10", "192.168.1.20", true, Description = "Same local subnet uses exact matching")]
    [TestCase(true, true, "203.0.113.100", "192.168.1.1", "192.168.1.2", true, Description = "Private remote IP uses exact matching")]
    public async Task ShouldContact_FiltersCorrectly(bool filterEnabled, bool subnetBucketing, string? externalIp,
        string addr1, string addr2, bool secondExpected)
    {
        RlpxHost host = CreateHost(filterEnabled, subnetBucketing, externalIp);
        try
        {
            Assert.That(host.ShouldContact(IPAddress.Parse(addr1)), Is.True, "first IP should be accepted");
            Assert.That(host.ShouldContact(IPAddress.Parse(addr2)), Is.EqualTo(secondExpected));
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task TrackSessionActivity_RefreshesFilterOnReceivedAndDeliveredMessages()
    {
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: true);
        try
        {
            IPAddress receivedIp = IPAddress.Parse("203.0.113.1");
            ISession receivedSession = Substitute.For<ISession>();
            receivedSession.Node.Returns(new Node(TestItem.PublicKeyA, receivedIp.ToString(), 30303));

            host.TrackSessionActivity(receivedSession);
            receivedSession.MsgReceived += Raise.EventWith(receivedSession, new PeerEventArgs(receivedSession.Node, "eth", 1, 32));

            Assert.That(host.ShouldContact(receivedIp), Is.False, "received traffic should keep the active session filtered");

            IPAddress deliveredIp = IPAddress.Parse("198.51.100.1");
            ISession deliveredSession = Substitute.For<ISession>();
            deliveredSession.Node.Returns(new Node(TestItem.PublicKeyA, deliveredIp.ToString(), 30303));

            host.TrackSessionActivity(deliveredSession);
            deliveredSession.MsgDelivered += Raise.EventWith(deliveredSession, new PeerEventArgs(deliveredSession.Node, "eth", 2, 64));

            Assert.That(host.ShouldContact(deliveredIp), Is.False, "sent traffic should keep the active session filtered");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ShouldContact_AlwaysAcceptsPrivilegedIp()
    {
        IPAddress privilegedIp = IPAddress.Parse("203.0.113.1");
        IPrivilegedIpProvider privilegedIpProvider = Substitute.For<IPrivilegedIpProvider>();
        privilegedIpProvider.IsPrivileged(privilegedIp).Returns(true);

        // Exact-match filtering would otherwise block the second attempt from the same IP.
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: false, privilegedIpProvider: privilegedIpProvider);
        try
        {
            Assert.That(host.ShouldContact(privilegedIp), Is.True, "first attempt accepted");
            Assert.That(host.ShouldContact(privilegedIp), Is.True, "privileged IP is never rate-limited");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ConnectAsync_falls_back_to_ipv6_endpoint_when_ipv4_connection_fails()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Ignore("IPv6 is not supported on this host");
        }

        using TcpListener ipv6Listener = new(IPAddress.IPv6Loopback, 0);
        ipv6Listener.Start();
        int listeningPort = ((IPEndPoint)ipv6Listener.LocalEndpoint).Port;

        // Bound but never listened to, so connections are refused while the port cannot be reused
        // by anything else running in parallel.
        using Socket refusingSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        refusingSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int refusedPort = ((IPEndPoint)refusingSocket.LocalEndPoint).Port;

        RlpxHost host = CreateHost(filterEnabled: false, subnetBucketing: false);
        try
        {
            await host.Init();

            // Channels are initialized before the TCP connect completes, so the failed primary
            // attempt creates a session as well; wait for the one established over IPv6.
            TaskCompletionSource<ISession> sessionCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.SessionCreated += (_, args) =>
            {
                if (args.Session.RemoteHost == "::1")
                {
                    sessionCreated.TrySetResult(args.Session);
                }
            };

            Node node = CreateDualStackNode(refusedPort, listeningPort);

            Assert.That(await host.ConnectAsync(node), Is.True, "the connection should fall back to the IPv6 endpoint");

            ISession session = await sessionCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(session.RemoteHost, Is.EqualTo("::1"));
                Assert.That(session.RemotePort, Is.EqualTo(listeningPort));
            }
        }
        finally
        {
            await host.Shutdown();
            ipv6Listener.Stop();
        }
    }

    [Test]
    public async Task ConnectAsync_reports_the_primary_endpoint_it_dialed()
    {
        using TcpListener ipv4Listener = new(IPAddress.Loopback, 0);
        ipv4Listener.Start();
        int listeningPort = ((IPEndPoint)ipv4Listener.LocalEndpoint).Port;

        RlpxHost host = CreateHost(filterEnabled: false, subnetBucketing: false);
        try
        {
            await host.Init();

            TaskCompletionSource<ISession> sessionCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.SessionCreated += (_, args) => sessionCreated.TrySetResult(args.Session);

            Node node = CreateDualStackNode(listeningPort, null);

            Assert.That(await host.ConnectAsync(node), Is.True);

            ISession session = await sessionCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(session.RemoteHost, Is.EqualTo("127.0.0.1"));
                Assert.That(session.RemotePort, Is.EqualTo(listeningPort));
            }
        }
        finally
        {
            await host.Shutdown();
            ipv4Listener.Stop();
        }
    }

    private static Node CreateDualStackNode(int? ipv4Port, int? ipv6Port)
    {
        NodeRecord enr = new();
        enr.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        if (ipv4Port is not null)
        {
            enr.SetEntry(new IpEntry(IPAddress.Loopback));
            enr.SetEntry(new TcpEntry(ipv4Port.Value));
        }

        if (ipv6Port is not null)
        {
            enr.SetEntry(new Ip6Entry(IPAddress.IPv6Loopback));
            enr.SetEntry(new Tcp6Entry(ipv6Port.Value));
        }

        Assert.That(Node.TryFromEnr(enr, out Node? node), Is.True);
        return node!;
    }

    private static RlpxHost CreateHost(bool filterEnabled, bool subnetBucketing, string? externalIp = null,
        IPrivilegedIpProvider? privilegedIpProvider = null)
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
            new StubHandshakeService(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            ipResolver,
            privilegedIpProvider ?? Substitute.For<IPrivilegedIpProvider>(),
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

    /// <summary>
    /// Keeps the outbound RLPx channel alive long enough for the connection attempt to complete;
    /// the tests never run a real handshake against the raw TCP listener.
    /// </summary>
    private sealed class StubHandshakeService : IHandshakeService
    {
        public Packet Auth(PublicKey remoteNodeId, EncryptionHandshake handshake, bool preEip8Format = false)
            => new([]);

        public Packet Ack(EncryptionHandshake handshake, Packet auth) => new([]);

        public void Agree(EncryptionHandshake handshake, Packet ack)
        {
        }
    }
}
