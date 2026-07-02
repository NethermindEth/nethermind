// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4;

public class DiscoveryAppTests
{
    [Test]
    public void Should_use_discovery_port_from_configured_enode_bootnode()
    {
        Enode enode = new(TestItem.PrivateKeyA.PublicKey, IPAddress.Parse("8.8.8.8"), 30303, discoveryPort: 9001);

        List<Node> bootNodes = DiscoveryApp.CreateBootNodes([enode.ToString()], LimboLogs.Instance.GetClassLogger<DiscoveryAppTests>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootNodes, Has.Count.EqualTo(1));
            Assert.That(bootNodes[0].Port, Is.EqualTo(9001));
            Assert.That(bootNodes[0].Host, Is.EqualTo("8.8.8.8"));
        }
    }
}
