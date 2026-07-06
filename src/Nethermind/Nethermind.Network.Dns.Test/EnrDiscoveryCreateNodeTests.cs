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
    private static NodeRecord CreateRecord(int? tcpPort, int? udpPort)
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        if (tcpPort is not null) record.SetEntry(new TcpEntry(tcpPort.Value));
        if (udpPort is not null) record.SetEntry(new UdpEntry(udpPort.Value));
        return record;
    }

    [Test]
    public void Creates_node_with_tcp_port()
    {
        Node? node = EnrDiscovery.CreateNode(CreateRecord(tcpPort: 30303, udpPort: 30304));

        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Port, Is.EqualTo(30303));
    }

    [Test]
    public void Skips_record_without_tcp_port() =>
        // A record without a tcp entry (e.g. a discovery-only bootnode) has no RLPx endpoint;
        // its udp port must not be used as a dial port.
        Assert.That(EnrDiscovery.CreateNode(CreateRecord(tcpPort: null, udpPort: 30303)), Is.Null);

    [Test]
    public void Skips_record_with_zero_tcp_port() =>
        Assert.That(EnrDiscovery.CreateNode(CreateRecord(tcpPort: 0, udpPort: 30303)), Is.Null);
}
