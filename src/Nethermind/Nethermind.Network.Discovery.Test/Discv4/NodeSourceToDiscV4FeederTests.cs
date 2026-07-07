// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Enr;
using Nethermind.Network.Test;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4;

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
        TaskCompletionSource nodeAdded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        discoveryApp.When(x => x.AddNodeToDiscovery(Arg.Any<Node>())).Do(_ => nodeAdded.TrySetResult());

        _ = feeder.Run();
        source.AddNode(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        await nodeAdded.Task.WaitAsync(token);

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
        TaskCompletionSource expectedNodesAdded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int addedNodes = 0;
        discoveryApp.When(x => x.AddNodeToDiscovery(Arg.Any<Node>())).Do(_ =>
        {
            if (Interlocked.Increment(ref addedNodes) == 10)
            {
                expectedNodesAdded.TrySetResult();
            }
        });

        _ = feeder.Run();
        for (int i = 0; i < 20; i++)
        {
            source.AddNode(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        }
        await expectedNodesAdded.Task.WaitAsync(token);

        discoveryApp.Received(10).AddNodeToDiscovery(Arg.Any<Node>());
    }

    [Test]
    [CancelAfter(1000)]
    public async Task Test_ShouldSkipNodeWithoutDiscoveryEndpointAndContinueToLimit(CancellationToken token)
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(token);
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, 1);
        TaskCompletionSource validNodeAdded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        discoveryApp.When(x => x.AddNodeToDiscovery(Arg.Any<Node>())).Do(_ => validNodeAdded.TrySetResult());
        Task feederTask = feeder.Run();
        Assert.That(Node.TryFromEnr(CreateTcpOnlyRecord(), out Node? tcpOnlyNode), Is.True);
        Node validNode = new(TestItem.PublicKeyA, TestItem.IPEndPointA);

        source.AddNode(tcpOnlyNode!);
        source.AddNode(validNode);
        await validNodeAdded.Task.WaitAsync(token);
        await feederTask.WaitAsync(token);

        discoveryApp.Received(1).AddNodeToDiscovery(Arg.Is<Node>(node => ReferenceEquals(node, validNode)));
        discoveryApp.DidNotReceive().AddNodeToDiscovery(Arg.Is<Node>(node => ReferenceEquals(node, tcpOnlyNode)));
    }

    private static NodeRecord CreateTcpOnlyRecord()
    {
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        nodeRecord.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        nodeRecord.SetEntry(new TcpEntry(30303));
        return nodeRecord;
    }
}
