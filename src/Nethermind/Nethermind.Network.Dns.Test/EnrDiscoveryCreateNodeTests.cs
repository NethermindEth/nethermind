// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class EnrDiscoveryCreateNodeTests
{
    // A record without a tcp entry (e.g. a discovery-only bootnode) has no RLPx endpoint;
    // its udp port must not be used as a dial port.
    [TestCase(30303, 30304, ExpectedResult = 30303)]
    [TestCase(null, 30303, ExpectedResult = null)]
    [TestCase(0, 30303, ExpectedResult = null)]
    public int? Creates_node_only_for_records_with_a_nonzero_tcp_port(int? tcpPort, int? udpPort)
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        if (tcpPort is not null) record.SetEntry(new TcpEntry(tcpPort.Value));
        if (udpPort is not null) record.SetEntry(new UdpEntry(udpPort.Value));

        return EnrDiscovery.CreateNode(record)?.Port;
    }
}
