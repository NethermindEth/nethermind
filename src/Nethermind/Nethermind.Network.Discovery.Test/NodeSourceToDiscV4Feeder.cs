// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Test;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class NodeSourceToDiscV4FeederTests
{
    [Test]
    [CancelAfter(1000)]
    public async Task Test_ShouldAddNodeToDiscover(CancellationToken token)
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(token);
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, 10);

        _ = feeder.Run();
        source.AddNode(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        await Task.Delay(100);

        discoveryApp.Received().AddNodeToDiscovery(Arg.Any<Node>());
    }

    [Test]
    [CancelAfter(1000)]
    public async Task Test_ShouldLimitAddedNode(CancellationToken token)
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(token);
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, 10);

        _ = feeder.Run();
        for (int i = 0; i < 20; i++)
        {
            source.AddNode(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        }
        await Task.Delay(100);

        discoveryApp.Received(10).AddNodeToDiscovery(Arg.Any<Node>());
    }
}
