// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class Discv5KademliaAdapterTests
{
    private IKademlia<PublicKey, Node> _kademlia = null!;

    [SetUp]
    public void SetUp() => _kademlia = Substitute.For<IKademlia<PublicKey, Node>>();

    [Test]
    public void GetNodesAtDistances_ShouldMapEachDistanceToKademliaTable()
    {
        Node nodeA = CreateNode(TestItem.PublicKeyA, 1);
        Node nodeB = CreateNode(TestItem.PublicKeyB, 2);
        Node nodeC = CreateNode(TestItem.PublicKeyC, 3);

        _kademlia.GetAllAtDistance(10).Returns([nodeA, nodeB]);
        _kademlia.GetAllAtDistance(11).Returns([nodeB, nodeC]);
        _kademlia.ClearReceivedCalls();

        Discv5KademliaAdapter adapter = CreateAdapter();

        Node[] result = adapter.GetNodesAtDistances([10, 11]);

        Assert.That(result, Is.EqualTo(new[] { nodeA, nodeB, nodeC }));
        _kademlia.Received(1).GetAllAtDistance(10);
        _kademlia.Received(1).GetAllAtDistance(11);
    }

    [Test]
    public void GetNodesAtDistances_ShouldExcludeRequester()
    {
        Node requester = CreateNode(TestItem.PublicKeyA, 1);
        Node returned = CreateNode(TestItem.PublicKeyB, 2);

        _kademlia.GetAllAtDistance(10).Returns([requester, returned]);

        Discv5KademliaAdapter adapter = CreateAdapter();

        Node[] result = adapter.GetNodesAtDistances([10], requester);

        Assert.That(result, Is.EqualTo(new[] { returned }));
    }

    [TestCase(-1)]
    [TestCase(257)]
    public void GetNodesAtDistances_ShouldRejectInvalidDistance(int distance)
    {
        Discv5KademliaAdapter adapter = CreateAdapter();

        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.GetNodesAtDistances([distance]));
    }

    [Test]
    public void TryAcceptChallenge_ShouldLimitBurstPerIp()
    {
        Discv5KademliaAdapter adapter = CreateAdapter();
        IPEndPoint endpoint = IPEndPoint.Parse("192.0.2.1:30303");

        Assert.That(adapter.TryAcceptChallenge(endpoint), Is.True);
        Assert.That(adapter.TryAcceptChallenge(endpoint), Is.True);
        Assert.That(adapter.TryAcceptChallenge(endpoint), Is.True);
        Assert.That(adapter.TryAcceptChallenge(endpoint), Is.True);
        Assert.That(adapter.TryAcceptChallenge(endpoint), Is.False);
    }

    private Discv5KademliaAdapter CreateAdapter() => new(
        new Lazy<IKademlia<PublicKey, Node>>(_kademlia),
        null!,
        null!,
        null!,
        new DiscoveryConfig(),
        new CryptoRandom(),
        LimboLogs.Instance);

    private static Node CreateNode(PublicKey publicKey, int hostSuffix) =>
        new(publicKey, $"192.168.1.{hostSuffix}", 30303);
}
