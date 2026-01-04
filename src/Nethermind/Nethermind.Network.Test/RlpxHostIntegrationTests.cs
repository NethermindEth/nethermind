// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class RlpxHostIntegrationTests
{
    [Test]
    public async Task ShouldContact_WithFilteringEnabled_BlocksSameIpWithinTimeout()
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = GetAvailablePort(),
            FilterPeersByRecentIp = true,
            MaxActivePeers = 50
        };

        RlpxHost host = new(
            Substitute.For<IMessageSerializationService>(),
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            LimboLogs.Instance);

        try
        {
            IPAddress ip = IPAddress.Parse("203.0.113.1");
            host.ShouldContact(ip).Should().BeTrue("first attempt should be accepted");
            host.ShouldContact(ip).Should().BeFalse("second attempt within timeout should be rejected");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ShouldContact_WithFilteringEnabled_AllowsDifferentIps()
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = GetAvailablePort(),
            FilterPeersByRecentIp = true,
            MaxActivePeers = 50
        };

        RlpxHost host = new(
            Substitute.For<IMessageSerializationService>(),
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            LimboLogs.Instance);

        try
        {
            IPAddress ip1 = IPAddress.Parse("203.0.113.1");
            IPAddress ip2 = IPAddress.Parse("198.51.100.1");

            host.ShouldContact(ip1).Should().BeTrue("first IP should be accepted");
            host.ShouldContact(ip2).Should().BeTrue("different IP should be accepted");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ShouldContact_WithFilteringEnabled_BlocksSameSubnet()
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = GetAvailablePort(),
            FilterPeersByRecentIp = true,
            FilterPeersBySameSubnet = true,
            MaxActivePeers = 50
        };

        RlpxHost host = new(
            Substitute.For<IMessageSerializationService>(),
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            LimboLogs.Instance);

        try
        {
            IPAddress ip1 = IPAddress.Parse("203.0.113.1");
            IPAddress ip2 = IPAddress.Parse("203.0.113.50"); // Same /24 subnet

            host.ShouldContact(ip1).Should().BeTrue("first IP in subnet should be accepted");
            host.ShouldContact(ip2).Should().BeFalse("second IP in same subnet should be rejected");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ShouldContact_WithFilteringDisabled_AlwaysReturnsTrue()
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = GetAvailablePort(),
            FilterPeersByRecentIp = false,
            MaxActivePeers = 50
        };

        RlpxHost host = new(
            Substitute.For<IMessageSerializationService>(),
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            LimboLogs.Instance);

        try
        {
            IPAddress ip = IPAddress.Parse("203.0.113.1");
            host.ShouldContact(ip).Should().BeTrue("filtering disabled, first attempt should be accepted");
            host.ShouldContact(ip).Should().BeTrue("filtering disabled, second attempt should still be accepted");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ShouldContact_WithExternalIp_UsesItForFiltering()
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = GetAvailablePort(),
            FilterPeersByRecentIp = true,
            FilterPeersBySameSubnet = true,
            ExternalIp = "192.168.1.100", // Private IP for testing exact matching
            MaxActivePeers = 50
        };

        RlpxHost host = new(
            Substitute.For<IMessageSerializationService>(),
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            LimboLogs.Instance);

        try
        {
            // IP in same subnet as private external IP
            IPAddress ip1 = IPAddress.Parse("192.168.1.10");
            IPAddress ip2 = IPAddress.Parse("192.168.1.20");

            // When current IP is private and remote is in same subnet, should use exact matching
            host.ShouldContact(ip1).Should().BeTrue("first IP should be accepted");
            host.ShouldContact(ip2).Should().BeTrue("different IP in same local subnet should be accepted (exact match)");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ShouldContact_WithPrivateRemoteIp_UsesExactMatching()
    {
        NetworkConfig networkConfig = new()
        {
            ProcessingThreadCount = 1,
            P2PPort = GetAvailablePort(),
            FilterPeersByRecentIp = true,
            FilterPeersBySameSubnet = true,
            ExternalIp = "203.0.113.100",
            MaxActivePeers = 50
        };

        RlpxHost host = new(
            Substitute.For<IMessageSerializationService>(),
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
            LimboLogs.Instance);

        try
        {
            // Private IPs should use exact matching
            IPAddress ip1 = IPAddress.Parse("192.168.1.1");
            IPAddress ip2 = IPAddress.Parse("192.168.1.2");

            host.ShouldContact(ip1).Should().BeTrue("first private IP should be accepted");
            host.ShouldContact(ip2).Should().BeTrue("different private IP should be accepted (exact match)");
        }
        finally
        {
            await host.Shutdown();
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
