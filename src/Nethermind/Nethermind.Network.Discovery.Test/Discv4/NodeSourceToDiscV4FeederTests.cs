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
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, CreateIpResolver(), processExitSource, 10);
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
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, CreateIpResolver(), processExitSource, 10);
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
    public async Task Test_ShouldNotAddNodeWhenLimitIsZero(CancellationToken token)
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(token);
        source.AddNode(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, CreateIpResolver(), processExitSource, 0);

        await feeder.Run().WaitAsync(token);

        discoveryApp.DidNotReceive().AddNodeToDiscovery(Arg.Any<Node>());
    }

    [Test]
    [CancelAfter(1000)]
    public async Task Test_ShouldSkipNodeWithoutDiscoveryEndpointAndContinueToLimit(CancellationToken token)
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(token);
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, CreateIpResolver(), processExitSource, 1);
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

    [Test]
    [CancelAfter(1000)]
    public async Task Test_ShouldForwardSignedEnrWhoseReachableDiscoveryEndpointNeedsFamilySelection(CancellationToken token)
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(token);
        NodeSourceToDiscV4Feeder feeder = new(
            source,
            discoveryApp,
            CreateIpResolver(IPAddress.Parse("2001:db8::5")),
            processExitSource,
            1);
        NodeRecord record = CreateAsymmetricDualStackRecord();
        Assert.That(Node.TryFromEnr(record, out Node? node), Is.True);
        Assert.That(node!.HasDiscoveryEndpoint, Is.False);

        Task feederTask = feeder.Run();
        source.AddNode(node);
        await feederTask.WaitAsync(token);

        discoveryApp.Received(1).AddNodeToDiscovery(Arg.Is<Node>(added =>
            added.Id.Equals(node.Id) &&
            added.Host == "2001:db8::1" &&
            added.Port == 30303 &&
            added.DiscoveryPort == 30304));
    }

    private static IIPResolver CreateIpResolver(IPAddress? localIp = null)
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(localIp ?? IPAddress.Any, IPAddress.Loopback)));
        return ipResolver;
    }

    private static NodeRecord CreateTcpOnlyRecord() =>
        TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("192.0.2.1"),
            tcpPort: 30303,
            udpPort: null);

    private static NodeRecord CreateAsymmetricDualStackRecord() =>
        TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyA,
            IPAddress.Parse("192.0.2.1"),
            tcpPort: 30303,
            udpPort: null,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
                record.SetEntry(new Udp6Entry(30304));
            });
}
