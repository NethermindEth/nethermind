// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class EnrDiscoveryCreateNodeTests
{
    [TestCase(30303, 30304, true, 30303, 30304, true)]
    [TestCase(30303, null, true, 30303, 30303, false)]
    [TestCase(null, 30304, false, null, null, false)]
    [TestCase(0, 30304, false, null, null, false)]
    public void TryCreateNode_creates_peer_candidate_with_optional_discovery_port(
        int? tcpPort,
        int? udpPort,
        bool expectedResult,
        int? expectedPort,
        int? expectedDiscoveryPort,
        bool expectedDiscoveryEndpoint)
    {
        NodeRecord nodeRecord = CreateNodeRecord(tcpPort, udpPort);

        bool result = EnrDiscovery.TryCreateNode(nodeRecord, out Node? node);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expectedResult));
            Assert.That(node is not null, Is.EqualTo(expectedResult));
            if (expectedResult)
            {
                Assert.That(node!.Host, Is.EqualTo("192.0.2.1"));
                Assert.That(node.Port, Is.EqualTo(expectedPort));
                Assert.That(node.DiscoveryPort, Is.EqualTo(expectedDiscoveryPort));
                Assert.That(node.HasDiscoveryEndpoint, Is.EqualTo(expectedDiscoveryEndpoint));
                Assert.That(node.Enr, Is.SameAs(nodeRecord));
            }
        }
    }

    private static NodeRecord CreateNodeRecord(int? tcpPort, int? udpPort)
    {
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        nodeRecord.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        if (tcpPort is { } tcp)
        {
            nodeRecord.SetEntry(new TcpEntry(tcp));
        }
        if (udpPort is { } udp)
        {
            nodeRecord.SetEntry(new UdpEntry(udp));
        }
        return nodeRecord;
    }
}
