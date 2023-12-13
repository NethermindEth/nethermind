// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class NodeSourceToDiscV4FeederTests
{
    [Test]
    public void Test_ShouldAddNodeToDiscover()
    {
        INodeSource source = Substitute.For<INodeSource>();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        using NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, 10);
        source.NodeAdded += Raise.EventWith(new NodeEventArgs(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA)));

        discoveryApp.Received().AddNodeToDiscovery(Arg.Any<Node>());
    }

    [Test]
    public void Test_ShouldLimitAddedNode()
    {
        INodeSource source = Substitute.For<INodeSource>();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        using NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, 10);

        for (int i = 0; i < 20; i++)
        {
            source.NodeAdded += Raise.EventWith(new NodeEventArgs(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA)));
        }

        discoveryApp.Received(10).AddNodeToDiscovery(Arg.Any<Node>());
    }
}
