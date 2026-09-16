// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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
    private const uint CompatibleForkHash = 0x11111111;
    private const uint IncompatibleForkHash = 0x22222222;

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

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldEmitPeerCandidateWithTcpEndpoint(bool mapDiscoveryAddressToIpv6, CancellationToken token)
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns(Array.Empty<Node>());
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);
        ValueTask<bool> firstMove = enumerator.MoveNextAsync();
        await Task.Yield();
        RaiseNode(kademlia, CreateNode(1, tcpPort: 30303, udpPort: 30304, mapDiscoveryAddressToIpv6: mapDiscoveryAddressToIpv6));

        Assert.That(await firstMove.AsTask(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumerator.Current.Id, Is.EqualTo(TestItem.PrivateKeys[1].PublicKey));
            Assert.That(enumerator.Current.Port, Is.EqualTo(30303));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldPreserveSelectedDiscoveryAddressFamily(CancellationToken token)
    {
        PrivateKey privateKey = TestItem.PrivateKeys[1];
        IPAddress ip6 = IPAddress.Parse("2001:db8::1");
        NodeRecord enr = TestEnrBuilder.BuildSigned(
            privateKey,
            IPAddress.Parse("192.0.2.1"),
            tcpPort: 30303,
            udpPort: 30304,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(ip6));
                record.SetEntry(new Tcp6Entry(30306));
                record.SetEntry(new Udp6Entry(30305));
                record.SetEntry(new EthEntry(new ForkId(CompatibleForkHash, 0).HashBytes, 0));
            });
        Assert.That(Node.TryFromDiscoveryEnr(enr, AddressFamily.InterNetworkV6, out Node? discoveryNode), Is.True);

        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns([discoveryNode!]);
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumerator.Current.Host, Is.EqualTo(ip6.ToString()));
            Assert.That(enumerator.Current.Port, Is.EqualTo(30306));
            Assert.That(enumerator.Current.DiscoveryPort, Is.EqualTo(30305));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldFallBackToAnotherTcpAddressFamily(CancellationToken token)
    {
        PrivateKey privateKey = TestItem.PrivateKeys[1];
        IPAddress ip4 = IPAddress.Parse("192.0.2.1");
        IPAddress ip6 = IPAddress.Parse("2001:db8::1");
        NodeRecord enr = TestEnrBuilder.BuildSigned(
            privateKey,
            ip4,
            tcpPort: null,
            udpPort: 30304,
            configureExtras: record =>
            {
                record.SetEntry(new Ip6Entry(ip6));
                record.SetEntry(new Tcp6Entry(30306));
                record.SetEntry(new EthEntry(new ForkId(CompatibleForkHash, 0).HashBytes, 0));
            });
        Assert.That(Node.TryFromDiscoveryEnr(enr, AddressFamily.InterNetwork, out Node? discoveryNode), Is.True);

        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns([discoveryNode!]);
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumerator.Current.Host, Is.EqualTo(ip6.ToString()));
            Assert.That(enumerator.Current.Port, Is.EqualTo(30306));
            Assert.That(enumerator.Current.DiscoveryPort, Is.EqualTo(30304));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldSkipEnrsWithoutEthEntry(bool includeEth2, CancellationToken token)
    {
        Node nonExecutionNode = CreateNode(1, includeEth2: includeEth2, ethForkHash: null);
        Node executionNode = CreateNode(2);
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns([nonExecutionNode, executionNode]);
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        Assert.That(enumerator.Current.Id, Is.EqualTo(TestItem.PrivateKeys[2].PublicKey));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldSkipEnrsWithIncompatibleForkId(CancellationToken token)
    {
        Node incompatibleNode = CreateNode(1, ethForkHash: IncompatibleForkHash);
        Node compatibleNode = CreateNode(2, ethForkHash: CompatibleForkHash);
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns([incompatibleNode, compatibleNode]);
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        Assert.That(enumerator.Current.Id, Is.EqualTo(TestItem.PrivateKeys[2].PublicKey));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldSkipRetainedEnrAfterNewerRecordWasObserved(CancellationToken token)
    {
        Node staleNode = CreateNode(1);
        NodeRecord staleRecord = staleNode.Enr ?? throw new AssertionException("Expected the test node to contain an ENR.");
        staleNode.ObserveEnrSequence(staleRecord.EnrSequence + 1);
        Node currentNode = CreateNode(2);
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns([staleNode, currentNode]);
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        Assert.That(enumerator.Current.Id, Is.EqualTo(currentNode.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldPreserveProvenanceAndObservedSequence(bool isVerified, CancellationToken token)
    {
        Node discoveryNode = CreateNode(1);
        NodeRecord record = discoveryNode.Enr ?? throw new AssertionException("Expected the test node to contain an ENR.");
        if (isVerified)
        {
            discoveryNode.SetVerifiedEnr(record);
        }
        else
        {
            discoveryNode.ObserveEnrSequence(record.EnrSequence);
        }

        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns([discoveryNode]);
        NodeSource source = CreateSource(kademlia);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enumerator.Current.Enr, Is.SameAs(record));
            Assert.That(enumerator.Current.IsVerifiedEnr(record), Is.EqualTo(isVerified));
            Assert.That(enumerator.Current.HighestObservedEnrSequence, Is.EqualTo(record.EnrSequence));
        }
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
            CreateForkInfo(),
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
            CreateForkInfo(),
            LimboLogs.Instance);
    }

    private static IForkInfo CreateForkInfo()
    {
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.IsForkIdCompatible(Arg.Any<ForkId>()).Returns(static call => call.Arg<ForkId>().ForkHash == CompatibleForkHash);
        return forkInfo;
    }

    private static Node CreateNode(
        int index,
        int tcpPort = 30303,
        int udpPort = 30304,
        bool includeEth2 = false,
        uint? ethForkHash = CompatibleForkHash,
        bool mapDiscoveryAddressToIpv6 = false)
    {
        PrivateKey privateKey = TestItem.PrivateKeys[index];
        string host = $"192.168.1.{index + 1}";
        IPAddress discoveryAddress = IPAddress.Parse(host);
        NodeRecord enr = TestEnrBuilder.BuildSigned(
            privateKey,
            IPAddress.Parse(host),
            tcpPort: tcpPort,
            udpPort: udpPort,
            configureExtras: enr =>
            {
                if (includeEth2) enr.SetEntry(new TestEth2Entry());
                if (ethForkHash is { } forkHash) enr.SetEntry(new EthEntry(new ForkId(forkHash, 0).HashBytes, 0));
            });
        return new Node(
            privateKey.PublicKey,
            mapDiscoveryAddressToIpv6 ? discoveryAddress.MapToIPv6().ToString() : host,
            tcpPort,
            udpPort)
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
