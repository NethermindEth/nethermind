// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Test;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class NodeSourceToDiscV4FeederTests
{
    [Test]
    public async Task Test_ShouldAddNodeToDiscover()
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, 10);

        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(1000);
        _ = feeder.Run(cts.Token);
        source.AddNode(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        await Task.Delay(100);

        discoveryApp.Received().AddNodeToDiscovery(Arg.Any<Node>());
    }

    [Test]
    public async Task Test_ShouldLimitAddedNode()
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, 10);

        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(1000);
        _ = feeder.Run(cts.Token);
        for (int i = 0; i < 20; i++)
        {
            source.AddNode(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        }
        await Task.Delay(100);

        discoveryApp.Received(10).AddNodeToDiscovery(Arg.Any<Node>());
    }
}
