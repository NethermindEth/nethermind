// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
    [TestCase(true, true, false, Description = "Exact match: blocks same IP")]
    [TestCase(false, true, true, Description = "Exact match: allows different IP")]
    public async Task ShouldContact_ExactMatch(bool sameIp, bool firstExpected, bool secondExpected)
    {
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: false);
        try
        {
            IPAddress ip1 = IPAddress.Parse("203.0.113.1");
            IPAddress ip2 = sameIp ? ip1 : IPAddress.Parse("198.51.100.1");

            host.ShouldContact(ip1).Should().Be(firstExpected);
            host.ShouldContact(ip2).Should().Be(secondExpected);
        }
        finally
        {
            await host.Shutdown();
        }
    }

    [Test]
    public async Task ShouldContact_WithSubnetBucketing_BlocksSameSubnet()
    {
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: true);
        try
        {
            IPAddress ip1 = IPAddress.Parse("203.0.113.1");
            IPAddress ip2 = IPAddress.Parse("203.0.113.50");

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
        RlpxHost host = CreateHost(filterEnabled: false, subnetBucketing: false);
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
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: true, externalIp: "192.168.1.100");
        try
        {
            IPAddress ip1 = IPAddress.Parse("192.168.1.10");
            IPAddress ip2 = IPAddress.Parse("192.168.1.20");

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
        RlpxHost host = CreateHost(filterEnabled: true, subnetBucketing: true, externalIp: "203.0.113.100");
        try
        {
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

        return new RlpxHost(
            Substitute.For<IMessageSerializationService>(),
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            Substitute.For<IHandshakeService>(),
            Substitute.For<ISessionMonitor>(),
            NullDisconnectsAnalyzer.Instance,
            networkConfig,
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
