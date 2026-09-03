// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
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
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, CreateListenerState(), 10);
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
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, CreateListenerState(), 10);
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
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, CreateListenerState(), 0);

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
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, CreateListenerState(), 1);
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
            processExitSource,
            CreateListenerState(IPAddress.Parse("2001:db8::5")),
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

    [Test]
    [CancelAfter(1000)]
    public async Task Test_ShouldUseBoundDiscoveryFamilyAfterFallback(CancellationToken token)
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(token);
        NetworkListenerState listenerState = CreateListenerState(IPAddress.IPv6Any, IPAddress.Any);
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, listenerState, 1);
        Task feederTask = feeder.Run();
        Node ipv6Node = new(TestItem.PublicKeyA, "2001:db8::1", 30303, 30304);
        Node ipv4Node = new(TestItem.PublicKeyB, "192.0.2.1", 30303, 30304);

        source.AddNode(ipv6Node);
        source.AddNode(ipv4Node);
        await feederTask.WaitAsync(token);

        discoveryApp.DidNotReceive().AddNodeToDiscovery(Arg.Is<Node>(node => ReferenceEquals(node, ipv6Node)));
        discoveryApp.Received(1).AddNodeToDiscovery(Arg.Is<Node>(node => ReferenceEquals(node, ipv4Node)));
    }

    [Test]
    public async Task Test_ShouldNotFeedNodesWithoutBoundDiscoveryListener()
    {
        TestNodeSource source = new();
        IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        NetworkListenerState listenerState = CreateListenerState(setBoundAddress: false);
        NodeSourceToDiscV4Feeder feeder = new(source, discoveryApp, processExitSource, listenerState, 1);
        source.AddNode(new Node(TestItem.PublicKeyA, TestItem.IPEndPointA));

        await feeder.Run();

        discoveryApp.DidNotReceive().AddNodeToDiscovery(Arg.Any<Node>());
    }

    private static IIPResolver CreateIpResolver(IPAddress? localIp = null)
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(localIp ?? IPAddress.Any, IPAddress.Loopback)));
        return ipResolver;
    }

    private static NetworkListenerState CreateListenerState(
        IPAddress? localIp = null,
        IPAddress? boundAddress = null,
        bool setBoundAddress = true)
    {
        IIPResolver ipResolver = CreateIpResolver(localIp);
        NetworkListenerState listenerState = new(new NetworkConfig(), ipResolver, LimboLogs.Instance);
        if (setBoundAddress)
        {
            listenerState.SetDiscoveryAddress(boundAddress ?? listenerState.PreferredAddress);
        }

        return listenerState;
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
