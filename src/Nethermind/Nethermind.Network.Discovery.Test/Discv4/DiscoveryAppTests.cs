// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4;

public class DiscoveryAppTests
{
    [Test]
    public void Should_use_discovery_port_from_configured_enode_bootnode()
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse("8.8.8.8"), 30303, discoveryPort: 9001);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes([new NetworkNode(enode)], LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Port, Is.EqualTo(30303));
            Assert.That(bootNodes[0].DiscoveryPort, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Host, Is.EqualTo("8.8.8.8"));
        }
    }

    [Test]
    public void Should_use_configured_enr_bootnode()
    {
        NodeRecord enr = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: null, udpPort: 9001);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes([new NetworkNode(enr.ToString())], LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Id, Is.EqualTo(TestItem.PrivateKeyA.PublicKey));
            Assert.That(bootNodes[0].Port, Is.Zero);
            Assert.That(bootNodes[0].DiscoveryPort, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Host, Is.EqualTo("8.8.8.8"));
            Assert.That(bootNodes[0].Enr?.ToString(), Is.EqualTo(enr.ToString()));
        }
    }

    [Test]
    public void Should_ignore_configured_enr_without_udp_endpoint()
    {
        NodeRecord enr = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"), tcpPort: 30303, udpPort: null);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes([new NetworkNode(enr.ToString())], LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>());

        Assert.That(bootNodes, Is.Empty);
    }
}
