// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class NodeSourceTests
{
    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldNotRetainDroppedNodesInRecentDedupe(CancellationToken token)
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns(Array.Empty<Node>());
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);
        ValueTask<bool> firstMove = enumerator.MoveNextAsync();
        await Task.Yield();
        Node firstNode = CreateNode(1);
        RaiseNode(kademlia, firstNode);

        Assert.That(await firstMove.AsTask(), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(firstNode));

        for (int i = 2; i < 66; i++)
        {
            RaiseNode(kademlia, CreateNode(i));
        }

        Node droppedNode = CreateNode(100);
        RaiseNode(kademlia, droppedNode);

        for (int i = 2; i < 66; i++)
        {
            Assert.That(await enumerator.MoveNextAsync(), Is.True);
        }

        ValueTask<bool> droppedMove = enumerator.MoveNextAsync();
        await Task.Yield();
        RaiseNode(kademlia, droppedNode);

        Assert.That(await droppedMove.AsTask(), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(droppedNode));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldEmitPeerCandidateWithTcpEndpoint(CancellationToken token)
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns(Array.Empty<Node>());
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);
        ValueTask<bool> firstMove = enumerator.MoveNextAsync();
        await Task.Yield();
        RaiseNode(kademlia, CreateNode(1, tcpPort: 30303, udpPort: 30304));

        Assert.That(await firstMove.AsTask(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumerator.Current.Id, Is.EqualTo(TestItem.PrivateKeys[1].PublicKey));
            Assert.That(enumerator.Current.Port, Is.EqualTo(30303));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldSkipConsensusOnlyEnrs(CancellationToken token)
    {
        Node consensusOnlyNode = CreateNode(1, includeEth2: true);
        Node executionNode = CreateNode(2);
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns([consensusOnlyNode, executionNode]);
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        Assert.That(enumerator.Current.Id, Is.EqualTo(TestItem.PrivateKeys[2].PublicKey));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldEmitPeerCandidateFromActiveKademliaDiscovery(CancellationToken token)
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns(Array.Empty<Node>());
        IKademliaDiscovery<PublicKey, Node> discovery = Substitute.For<IKademliaDiscovery<PublicKey, Node>>();
        discovery.DiscoverNodes(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncEnumerable(CreateNode(1, tcpPort: 30303, udpPort: 30304)));

        NodeSource source = new(
            kademlia,
            discovery,
            new DiscoveryConfig { ConcurrentDiscoveryJob = 1 },
            new KademliaConfig<Node> { CurrentNodeId = CreateNode(0) },
            ExecutionLayerDiscv5RecordFilter.Instance,
            new TestEnrForkIdFilter(),
            LimboLogs.Instance);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumerator.Current.Id, Is.EqualTo(TestItem.PrivateKeys[1].PublicKey));
            Assert.That(enumerator.Current.Port, Is.EqualTo(30303));
        }
    }

    private static NodeSource CreateSource(IKademlia<PublicKey, Node> kademlia)
    {
        IKademliaDiscovery<PublicKey, Node> discovery = Substitute.For<IKademliaDiscovery<PublicKey, Node>>();
        discovery.DiscoverNodes(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncEnumerable<Node>());

        return new NodeSource(
            kademlia,
            discovery,
            new DiscoveryConfig { ConcurrentDiscoveryJob = 0 },
            new KademliaConfig<Node> { CurrentNodeId = CreateNode(0) },
            ExecutionLayerDiscv5RecordFilter.Instance,
            new TestEnrForkIdFilter(),
            LimboLogs.Instance);
    }

    private static Node CreateNode(int index, int tcpPort = 30303, int udpPort = 30304, bool includeEth2 = false)
    {
        PrivateKey privateKey = TestItem.PrivateKeys[index];
        string host = $"192.168.1.{index + 1}";
        NodeRecord enr = TestEnrBuilder.BuildSigned(
            privateKey,
            IPAddress.Parse(host),
            tcpPort: tcpPort,
            udpPort: udpPort,
            configureExtras: includeEth2 ? static enr => enr.SetEntry(new TestEth2Entry()) : null);
        return new Node(privateKey.PublicKey, host, udpPort)
        {
            Enr = enr
        };
    }

    private static void RaiseNode(IKademlia<PublicKey, Node> kademlia, Node node) =>
        kademlia.OnNodeAdded += Raise.Event<EventHandler<Node>>(null, node);

    private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(params IEnumerable<T> items)
    {
        foreach (T item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
