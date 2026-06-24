// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Discovery.Discv5.Packets;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class KademliaAdapterTests
{
    private IKademlia<PublicKey, Node> _kademlia = null!;
    private PacketCodec? _packetCodec;

    [SetUp]
    public void SetUp() => _kademlia = Substitute.For<IKademlia<PublicKey, Node>>();

    [TearDown]
    public void TearDown()
    {
        _packetCodec?.Dispose();
        _packetCodec = null;
    }

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

    [TestCaseSource(nameof(AcceptableNodeRecordCases))]
    public void IsAcceptableNodeRecord_ShouldValidateRecord(AcceptableNodeRecordCase testCase)
    {
        NodeRecord record = CreateEnr(testCase.PrivateKey, testCase.IpAddress, includeEth2: testCase.IncludeEth2);

        Assert.That(
            KademliaAdapter.IsAcceptableNodeRecord(
                NodeRecord.FromEnrString(record.EnrString),
                testCase.ExpectedNodeId,
                testCase.AllowNonRoutable,
                ExecutionLayerDiscv5RecordFilter.Instance),
            Is.EqualTo(testCase.ExpectedResult));
    }

    private KademliaAdapter CreateAdapter()
    {
        INodeRecordProvider nodeRecordProvider = Substitute.For<INodeRecordProvider>();
        nodeRecordProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<NodeRecord>(CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback)));
        _packetCodec?.Dispose();
        _packetCodec = new PacketCodec(
            new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            new CryptoRandom(),
            new EthereumEcdsa(0));

        return new(
            new Lazy<IKademlia<PublicKey, Node>>(_kademlia),
            new NettyDiscoveryV5Handler(LimboLogs.Instance),
            _packetCodec,
            nodeRecordProvider,
            new DiscoveryConfig(),
            new CryptoRandom(),
            Hash256KademliaDistance.Instance,
            ExecutionLayerDiscv5RecordFilter.Instance,
            LimboLogs.Instance);
    }

    private static Node CreateNode(PublicKey publicKey, int hostSuffix) =>
        new(publicKey, $"192.168.1.{hostSuffix}", 30303);

    private static NodeRecord CreateEnr(PrivateKey privateKey, IPAddress ipAddress, ulong enrSequence = 1, bool includeEth2 = false) =>
        TestEnrBuilder.BuildSigned(
            privateKey,
            ipAddress,
            tcpPort: null,
            enrSequence: enrSequence,
            configureExtras: includeEth2 ? static enr => enr.SetEntry(new TestEth2Entry()) : null);

    private static IEnumerable<TestCaseData> AcceptableNodeRecordCases()
    {
        yield return new TestCaseData(new AcceptableNodeRecordCase(
            TestItem.PrivateKeyB,
            IPAddress.Parse("192.0.2.1"),
            TestItem.PrivateKeyB.PublicKey.Hash,
            AllowNonRoutable: true,
            IncludeEth2: false,
            ExpectedResult: false)).SetName("Rejects special-use record");
        yield return new TestCaseData(new AcceptableNodeRecordCase(
            TestItem.PrivateKeyB,
            IPAddress.Parse("8.8.8.8"),
            TestItem.PrivateKeyA.PublicKey.Hash,
            AllowNonRoutable: false,
            IncludeEth2: false,
            ExpectedResult: false)).SetName("Rejects node-id mismatch");
        yield return new TestCaseData(new AcceptableNodeRecordCase(
            TestItem.PrivateKeyB,
            IPAddress.Loopback,
            TestItem.PrivateKeyB.PublicKey.Hash,
            AllowNonRoutable: true,
            IncludeEth2: false,
            ExpectedResult: true)).SetName("Allows non-routable when requested");
        yield return new TestCaseData(new AcceptableNodeRecordCase(
            TestItem.PrivateKeyB,
            IPAddress.Parse("8.8.8.8"),
            TestItem.PrivateKeyB.PublicKey.Hash,
            AllowNonRoutable: false,
            IncludeEth2: true,
            ExpectedResult: false)).SetName("Rejects consensus-only record");
    }

    public readonly record struct AcceptableNodeRecordCase(
        PrivateKey PrivateKey,
        IPAddress IpAddress,
        Hash256 ExpectedNodeId,
        bool AllowNonRoutable,
        bool IncludeEth2,
        bool ExpectedResult);
}
