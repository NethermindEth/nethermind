// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    [Test]
    public void TryGetKnownSignedRecord_ShouldScanOnlyMatchingBucket()
    {
        Node current = CreateNode(TestItem.PublicKeyA, 1);
        Node target = CreateNode(TestItem.PublicKeyB, 2);
        Node sameBucketNode = CreateNode(TestItem.PublicKeyC, 3);
        Node otherBucketNode = CreateNode(TestItem.PublicKeyD, 4);
        target.Enr = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse("8.8.8.8"));
        int targetDistance = Hash256KademliaDistance.Instance.CalculateLogDistance(current.Id.Hash, target.Id.Hash);
        int otherDistance = targetDistance == Hash256KademliaDistance.Instance.MaxDistance
            ? targetDistance - 1
            : targetDistance + 1;
        _kademlia.GetAllAtDistance(targetDistance).Returns([sameBucketNode, target]);
        _kademlia.GetAllAtDistance(otherDistance).Returns([otherBucketNode]);
        _kademlia.ClearReceivedCalls();

        KademliaAdapter adapter = CreateAdapter(current);

        bool result = adapter.TryGetKnownSignedRecord(target.Id.Hash.ValueHash256, out NodeRecord? record);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(record, Is.SameAs(target.Enr));
        }

        _kademlia.Received(1).GetAllAtDistance(targetDistance);
        _kademlia.DidNotReceive().GetAllAtDistance(otherDistance);
        _kademlia.DidNotReceive().IterateNodes();
    }

    [Test]
    public void HasDiscoveryEndpoint_ShouldRequireExactEndpoint()
    {
        IPEndPoint endpoint = IPEndPoint.Parse("172.19.0.2:30304");
        NodeRecord record = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            endpoint.Address,
            tcpPort: null,
            udpPort: endpoint.Port);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(record, endpoint), Is.True);
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(record, new IPEndPoint(endpoint.Address.MapToIPv6(), endpoint.Port)), Is.True);
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(record, IPEndPoint.Parse("172.17.0.1:30304")), Is.False);
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(record, IPEndPoint.Parse("172.19.0.2:30305")), Is.False);
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(record, new IPEndPoint(endpoint.Address.MapToIPv6(), 30305)), Is.False);
        }
    }

    [Test]
    public void HasDiscoveryEndpoint_ShouldMatchBothFamiliesInDualStackRecord()
    {
        IPAddress ip = IPAddress.Parse("172.19.0.2");
        IPAddress ip6 = IPAddress.Parse("2001:db8::1");
        NodeRecord record = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            ip,
            tcpPort: null,
            udpPort: 30304,
            configureExtras: enr =>
            {
                enr.SetEntry(new Ip6Entry(ip6));
                enr.SetEntry(new Udp6Entry(30305));
            });
        NodeRecord fallbackRecord = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            ip,
            tcpPort: null,
            udpPort: 30304,
            configureExtras: enr => enr.SetEntry(new Ip6Entry(ip6)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(record, new IPEndPoint(ip, 30304)), Is.True);
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(record, new IPEndPoint(ip6, 30305)), Is.True);
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(record, new IPEndPoint(ip6, 30304)), Is.False);
            Assert.That(KademliaAdapter.HasDiscoveryEndpoint(fallbackRecord, new IPEndPoint(ip6, 30304)), Is.True);
        }
    }

    [Test]
    public void HasDiscoveryEndpoint_RejectsNativeIpv6InIpEntry()
    {
        // Decoding does not enforce the 4-byte length of the `ip` key, so a peer can put a native IPv6
        // address there; the family check must reject it rather than match it as IPv4.
        NodeRecord record = new();
        record.SetEntry(new IpEntry(IPAddress.Parse("2001:db8::1")));
        record.SetEntry(new UdpEntry(30304));

        Assert.That(
            KademliaAdapter.HasDiscoveryEndpoint(record, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 30304)),
            Is.False);
    }

    [TestCaseSource(nameof(AcceptableNodeRecordCases))]
    public void IsAcceptableNodeRecord_ShouldValidateRecord(AcceptableNodeRecordCase testCase)
    {
        NodeRecord record = CreateEnr(testCase.PrivateKey, testCase.IpAddress, includeEth2: testCase.IncludeEth2);

        Assert.That(
            KademliaAdapter.IsAcceptableNodeRecord(
                NodeRecord.FromEnrString(record.ToString()),
                testCase.ExpectedNodeId,
                testCase.AllowNonRoutable),
            Is.EqualTo(testCase.ExpectedResult));
    }

    [TestCase("10.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1111", 30306, 30305)]
    [TestCase("8.8.8.8", "fd00::1", "8.8.8.8", 30303, 30304)]
    public void TryGetAcceptableNode_SelectsRoutableFamily(
        string ip,
        string ip6,
        string expectedIp,
        int expectedTcpPort,
        int expectedUdpPort)
    {
        NodeRecord record = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            IPAddress.Parse(ip),
            tcpPort: 30303,
            udpPort: 30304,
            configureExtras: enr =>
            {
                enr.SetEntry(new Ip6Entry(IPAddress.Parse(ip6)));
                enr.SetEntry(new Tcp6Entry(30306));
                enr.SetEntry(new Udp6Entry(30305));
            });

        bool result = KademliaAdapter.TryGetAcceptableNode(
            record,
            allowNonRoutable: false,
            localIp: IPAddress.IPv6Any,
            node: out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node!.Host, Is.EqualTo(expectedIp));
            Assert.That(node.Port, Is.EqualTo(expectedTcpPort));
            Assert.That(node.DiscoveryPort, Is.EqualTo(expectedUdpPort));
            Assert.That(KademliaAdapter.IsAcceptableNodeRecord(record, node.Id.Hash, allowNonRoutable: false), Is.True);
        }
    }

    [Test]
    public void TryGetAcceptableNode_PreservesPreferredIpv6FamilyWhenEndpointChanges()
    {
        IPAddress ip6 = IPAddress.Parse("2606:4700:4700::1111");
        NodeRecord record = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            IPAddress.Parse("8.8.8.8"),
            tcpPort: 30303,
            udpPort: 30304,
            configureExtras: enr => enr.SetEntry(new Ip6Entry(ip6)));

        bool result = KademliaAdapter.TryGetAcceptableNode(
            record,
            allowNonRoutable: false,
            localIp: IPAddress.IPv6Any,
            preferredEndpoint: new IPEndPoint(ip6, 40404),
            node: out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node!.Host, Is.EqualTo(ip6.ToString()));
            Assert.That(node.Port, Is.EqualTo(30303));
            Assert.That(node.DiscoveryPort, Is.EqualTo(30304));
        }
    }

    [TestCase("0.0.0.0", "8.8.8.8")]
    [TestCase("::1", "2606:4700:4700::1111")]
    [TestCase("::", "8.8.8.8")]
    public void TryGetAcceptableNode_SelectsFamilyReachableByLocalListener(string localIp, string expectedIp)
    {
        NodeRecord record = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            IPAddress.Parse("8.8.8.8"),
            tcpPort: 30303,
            udpPort: 30304,
            configureExtras: enr =>
            {
                enr.SetEntry(new Ip6Entry(IPAddress.Parse("2606:4700:4700::1111")));
                enr.SetEntry(new Tcp6Entry(30306));
                enr.SetEntry(new Udp6Entry(30305));
            });

        bool result = KademliaAdapter.TryGetAcceptableNode(
            record,
            allowNonRoutable: false,
            localIp: IPAddress.Parse(localIp),
            node: out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        Assert.That(node!.Host, Is.EqualTo(expectedIp));
    }

    [TestCase("0.0.0.0", "2001:4860:4860::8888")]
    [TestCase("::1", "8.8.8.8")]
    public void TryGetAcceptableNode_RejectsEndpointOutsideLocalListenerAddressFamily(string localIp, string remoteIp)
    {
        NodeRecord record = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            IPAddress.Parse(remoteIp),
            tcpPort: null,
            udpPort: 30304);

        bool result = KademliaAdapter.TryGetAcceptableNode(
            record,
            allowNonRoutable: false,
            localIp: IPAddress.Parse(localIp),
            node: out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    [Test]
    public async Task RefreshRemoteRecord_DoesNotRefetchValidRecordWithoutUsableEndpoint()
    {
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse("2001:db8::1"), enrSequence: 2);
        RejectingRefreshAdapter adapter = new(record);
        Node node = CreateNode(TestItem.PublicKeyB, 2);

        await adapter.Refresh(node, record.EnrSequence);
        await adapter.Refresh(node, record.EnrSequence);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(adapter.RequestCount, Is.EqualTo(1));
            Assert.That(node.Enr, Is.SameAs(record));
            Assert.That(node.RequestingEnrSequence, Is.Zero);
        }
    }

    private KademliaAdapter CreateAdapter(Node? currentNode = null, IPAddress? localIp = null)
    {
        currentNode ??= CreateNode(TestItem.PublicKeyA, 1);
        INodeRecordProvider nodeRecordProvider = Substitute.For<INodeRecordProvider>();
        nodeRecordProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<NodeRecord>(CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback)));
        IIPResolver ipResolver = CreateIpResolver(localIp ?? IPAddress.IPv6Any);
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
            ipResolver,
            new DiscoveryConfig(),
            new KademliaConfig<Node> { CurrentNodeId = currentNode },
            new CryptoRandom(),
            Hash256KademliaDistance.Instance,
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

    private static IIPResolver CreateIpResolver(IPAddress localIp)
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
            new IIPResolver.NethermindIp(localIp, IPAddress.Loopback)));
        return ipResolver;
    }

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
            ExpectedResult: true)).SetName("Allows consensus-only routing record");
    }

    private sealed class RejectingRefreshAdapter(NodeRecord record)
        : KademliaAdapterBase("test", CreateIpResolver(IPAddress.Any), LimboLogs.Instance.GetClassLogger<RejectingRefreshAdapter>())
    {
        public int RequestCount { get; private set; }

        public Task Refresh(Node node, ulong sequence)
            => RefreshRemoteRecordIfNewer(node, sequence, CancellationToken.None);

        protected override ValueTask<NodeRecord?> RequestRemoteRecord(Node node, ulong requestedSequence, CancellationToken token)
        {
            RequestCount++;
            return new ValueTask<NodeRecord?>(record);
        }

        protected override bool TryCreateNodeFromEnr(
            Node currentNode,
            NodeRecord refreshedRecord,
            [NotNullWhen(true)] out Node? refreshedNode)
        {
            refreshedNode = null;
            return false;
        }

        protected override void AddOrRefreshRemoteNode(Node node)
        {
        }
    }

    public readonly record struct AcceptableNodeRecordCase(
        PrivateKey PrivateKey,
        IPAddress IpAddress,
        Hash256 ExpectedNodeId,
        bool AllowNonRoutable,
        bool IncludeEth2,
        bool ExpectedResult);
}
