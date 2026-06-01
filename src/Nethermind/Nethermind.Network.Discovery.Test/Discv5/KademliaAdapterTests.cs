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
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class KademliaAdapterTests
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

        KademliaAdapter adapter = CreateAdapter();

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

        KademliaAdapter adapter = CreateAdapter();

        Node[] result = adapter.GetNodesAtDistances([10], requester);

        Assert.That(result, Is.EqualTo(new[] { returned }));
    }

    [TestCase(-1)]
    [TestCase(257)]
    public void GetNodesAtDistances_ShouldRejectInvalidDistance(int distance)
    {
        KademliaAdapter adapter = CreateAdapter();

        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.GetNodesAtDistances([distance]));
    }

    [Test]
    public void TryAcceptChallenge_ShouldLimitBurstPerIp()
    {
        KademliaAdapter adapter = CreateAdapter();
        IPEndPoint endpoint = IPEndPoint.Parse("192.0.2.1:30303");

        for (int i = 0; i < 16; i++)
        {
            Assert.That(adapter.TryAcceptChallenge(endpoint), Is.True);
        }

        Assert.That(adapter.TryAcceptChallenge(endpoint), Is.False);
    }

    [Test]
    public void IsAcceptableNodeRecord_ShouldRejectSpecialUseRecord()
    {
        NodeRecord documentationRecord = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse("192.0.2.1"));

        Assert.That(
            KademliaAdapter.IsAcceptableNodeRecord(
                documentationRecord,
                TestItem.PrivateKeyB.PublicKey.Hash,
                allowNonRoutable: true),
            Is.False);
    }

    [Test]
    public void IsAcceptableNodeRecord_ShouldRejectNodeIdMismatch()
    {
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse("8.8.8.8"));

        Assert.That(
            KademliaAdapter.IsAcceptableNodeRecord(
                record,
                TestItem.PrivateKeyA.PublicKey.Hash,
                allowNonRoutable: false),
            Is.False);
    }

    [Test]
    public void IsAcceptableNodeRecord_ShouldAllowNonRoutableWhenRequested()
    {
        NodeRecord loopbackRecord = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);

        Assert.That(
            KademliaAdapter.IsAcceptableNodeRecord(
                loopbackRecord,
                TestItem.PrivateKeyB.PublicKey.Hash,
                allowNonRoutable: true),
            Is.True);
    }

    private KademliaAdapter CreateAdapter() => new(
        new Lazy<IKademlia<PublicKey, Node>>(_kademlia),
        null!,
        null!,
        null!,
        new DiscoveryConfig(),
        new CryptoRandom(),
        Hash256KademliaDistance.Instance,
        LimboLogs.Instance);

    private static Node CreateNode(PublicKey publicKey, int hostSuffix) =>
        new(publicKey, $"192.168.1.{hostSuffix}", 30303);

    private static NodeRecord CreateEnr(PrivateKey privateKey, IPAddress ipAddress)
    {
        NodeRecord enr = new();
        enr.SetEntry(IdEntry.Instance);
        enr.SetEntry(new IpEntry(ipAddress));
        enr.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        enr.SetEntry(new UdpEntry(30303));
        enr.EnrSequence = 1;
        new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(enr);
        return enr;
    }

}
