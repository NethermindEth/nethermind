// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4;

[TestFixture]
public class DiscV4KademliaModuleTests
{
    [Test]
    public void CurrentNodeId_uses_enode_ip_and_discovery_port()
    {
        IEnode enode = new Enode(TestItem.PublicKeyA, IPAddress.Parse("10.0.0.5"), 30303);
        INetworkConfig networkConfig = new NetworkConfig
        {
            DiscoveryPort = 30304,
            P2PPort = 30303,
        };

        ContainerBuilder containerBuilder = new();
        containerBuilder.RegisterInstance(new DiscoveryConfig()).As<IDiscoveryConfig>();
        containerBuilder.AddModule(new DiscV4KademliaModule(enode, networkConfig, []));
        using IContainer container = containerBuilder.Build();

        KademliaConfig<Node> config = container.Resolve<KademliaConfig<Node>>();

        Assert.That(config.CurrentNodeId.Id, Is.EqualTo(enode.PublicKey));
        Assert.That(config.CurrentNodeId.Host, Is.EqualTo("10.0.0.5"));
        Assert.That(config.CurrentNodeId.Port, Is.EqualTo(30304));
    }
}
