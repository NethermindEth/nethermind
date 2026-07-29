// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class NetworkNodePortTests
{
    private static NetworkNode CreateEnrNetworkNode(int? tcpPort, int udpPort)
    {
        NodeRecord enr = new();
        enr.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        enr.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        if (tcpPort is { } tcp) enr.SetEntry(new TcpEntry(tcp));
        enr.SetEntry(new UdpEntry(udpPort));
        enr.EnrSequence = 1;
        new NodeRecordSigner(new EthereumEcdsa(0), TestItem.PrivateKeyA).Sign(enr);
        return new NetworkNode(enr.ToString());
    }

    [TestCase(30303, 30301, ExpectedResult = 30303)]
    [TestCase(null, 30301, ExpectedResult = 0)]
    public int Port_of_enr_backed_node_is_the_rlpx_tcp_port(int? tcpPort, int udpPort) =>
        CreateEnrNetworkNode(tcpPort, udpPort).Port;

    [TestCase(30303, 30301, ExpectedResult = 30301)]
    [TestCase(null, 30301, ExpectedResult = 30301)]
    public int DiscoveryPort_of_enr_backed_node_is_the_udp_port(int? tcpPort, int udpPort) =>
        CreateEnrNetworkNode(tcpPort, udpPort).DiscoveryPort;

    [Test]
    public void Node_created_from_enr_backed_network_node_dials_the_tcp_port()
    {
        Node node = new(CreateEnrNetworkNode(tcpPort: 30303, udpPort: 30301));

        Assert.That(node.Port, Is.EqualTo(30303));
    }

    [TestCase("enode://{0}@192.0.2.1:30303", 30303, 30303)]
    [TestCase("enode://{0}@192.0.2.1:30303?discport=30301", 30303, 30301)]
    [TestCase("enode://{0}@192.0.2.1:0?discport=30301", 0, 30301)]
    public void Enode_backed_node_keeps_single_port_and_discport_semantics(
        string enodeFormat, int expectedPort, int expectedDiscoveryPort)
    {
        NetworkNode networkNode = new(string.Format(enodeFormat, TestItem.PublicKeyA.ToString(false)));

        Assert.That(networkNode.Port, Is.EqualTo(expectedPort));
        Assert.That(networkNode.DiscoveryPort, Is.EqualTo(expectedDiscoveryPort));
    }
}
