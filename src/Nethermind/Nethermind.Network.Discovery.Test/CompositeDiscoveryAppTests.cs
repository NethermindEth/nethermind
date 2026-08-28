// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class CompositeDiscoveryAppTests
{
    [TestCase("0.0.0.0", AddressFamily.InterNetwork, false)]
    [TestCase("127.0.0.1", AddressFamily.InterNetwork, false)]
    [TestCase("::1", AddressFamily.InterNetworkV6, false)]
    [TestCase("::", AddressFamily.InterNetworkV6, true)]
    [TestCase("::ffff:0.0.0.0", AddressFamily.InterNetworkV6, true)]
    public void CreateDatagramSocket_MatchesListenerAddress(string localIp, AddressFamily expectedFamily, bool expectedDualMode)
    {
        using Socket socket = CompositeDiscoveryApp.CreateDatagramSocket(IPAddress.Parse(localIp));

        Assert.That(socket.AddressFamily, Is.EqualTo(expectedFamily));
        if (expectedFamily == AddressFamily.InterNetworkV6)
        {
            Assert.That(socket.DualMode, Is.EqualTo(expectedDualMode));
        }
    }

    [TestCase("0.0.0.0", "2001:db8::1", "192.0.2.1", 30304)]
    [TestCase("2001:db8::5", "192.0.2.1", "2001:db8::1", 30305)]
    [TestCase("::", "2001:db8::1", "2001:db8::1", 30305)]
    public void TryCreateReachableDiscoveryNode_SelectsReachableFamily(
        string localIp,
        string preferredIp,
        string expectedIp,
        int expectedDiscoveryPort)
    {
        NodeRecord record = CreateDualStackRecord();

        bool result = CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
            record,
            IPAddress.Parse(localIp),
            new IPEndPoint(IPAddress.Parse(preferredIp), 40404),
            out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node!.Host, Is.EqualTo(expectedIp));
            Assert.That(node.DiscoveryPort, Is.EqualTo(expectedDiscoveryPort));
        }
    }

    [Test]
    public void TryCreateReachableDiscoveryNode_RejectsFamilyOutsideListener()
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
        record.SetEntry(new Udp6Entry(30305));

        bool result = CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
            record,
            IPAddress.Any,
            preferredEndpoint: null,
            out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    private static NodeRecord CreateDualStackRecord()
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        record.SetEntry(new TcpEntry(30303));
        record.SetEntry(new UdpEntry(30304));
        record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
        record.SetEntry(new Tcp6Entry(30306));
        record.SetEntry(new Udp6Entry(30305));
        return record;
    }
}
