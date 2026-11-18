// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Network.Discovery.Test.Discv5.Handlers;

public class NodesResponseHandlerTests
{
    [Test]
    public void Record_verification_is_bounded_across_repeated_messages()
    {
        Node receiver = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        CountingEcdsa crypto = new();
        using NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record, IPAddress.Loopback, new NodeRecordSigner(crypto));
        using NodesMsg message = new([1], 16, Enumerable.Repeat(record, 16).ToArray());

        for (int index = 0; index < 17; index++) handler.Handle(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(crypto.Recoveries, Is.EqualTo(256));
            Assert.That(handler.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(handler.GetNodes(), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public void Changing_total_completes_without_verifying_the_changed_message()
    {
        Node receiver = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        CountingEcdsa crypto = new();
        using NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record, IPAddress.Loopback, new NodeRecordSigner(crypto));
        using NodesMsg first = new([1], 2, [record]);
        using NodesMsg changed = new([1], 3, [record]);

        handler.Handle(first);
        handler.Handle(changed);
        handler.Handle(first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(crypto.Recoveries, Is.EqualTo(1));
            Assert.That(handler.Task.IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public void Invalid_total_does_not_verify_records([Values(0, 17)] int total)
    {
        Node receiver = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        CountingEcdsa crypto = new();
        using NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record, IPAddress.Loopback, new NodeRecordSigner(crypto));
        using NodesMsg message = new([1], total, [record]);

        handler.Handle(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(crypto.Recoveries, Is.Zero);
            Assert.That(handler.Task.IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public void Disposed_handler_does_not_verify_records()
    {
        Node receiver = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        CountingEcdsa crypto = new();
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record, IPAddress.Loopback, new NodeRecordSigner(crypto));
        using NodesMsg message = new([1], 1, [record]);

        handler.Dispose();
        handler.Handle(message);

        Assert.That(crypto.Recoveries, Is.Zero);
    }
    [Test]
    public void Invalid_curve_point_does_not_hide_the_following_honest_record()
    {
        Node receiver = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeRecord honest = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyB, IPAddress.Loopback);
        NodeRecord invalid = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyC, IPAddress.Loopback);
        byte[] badPoint = new byte[33];
        Array.Fill(badPoint, (byte)255);
        badPoint[0] = 2;
        invalid.SetEntry(new SecP256k1Entry(new CompressedPublicKey(badPoint)));
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyC).Sign(invalid);
        using Distances requested = CreateDistances(receiver, honest);
        CountingEcdsa crypto = new();
        using NodesResponseHandler handler = new(receiver, requested, ValueHash256KademliaDistance.Instance,
            IPAddress.Loopback, CreateDevnetPolicy().AllowedAddresses, recordSigner: new NodeRecordSigner(crypto));
        using NodesMsg message = new([1], 1, [invalid, honest]);

        handler.Handle(message);
        Node[] nodes = handler.GetNodes();

        Assert.That(nodes, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes[0].Id, Is.EqualTo(TestItem.PublicKeyB));
            Assert.That(crypto.Recoveries, Is.EqualTo(1));
        }
    }

    [Test]
    public void Irrelevant_records_are_filtered_before_signature_recovery(
        [Values("off-distance", "self", "honest", "invalid-signature")] string kind)
    {
        Node receiver = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyB, IPAddress.Loopback);
        if (kind == "invalid-signature") record.Signature = new Ecdsa().Sign(TestItem.PrivateKeyC, record.ContentHash);
        int actualDistance = GetDistance(receiver, record);
        using Distances requested = new([kind == "off-distance" ? (actualDistance + 1) % 257 : actualDistance]);
        CountingEcdsa crypto = new();
        using NodesResponseHandler handler = new(receiver, requested, ValueHash256KademliaDistance.Instance,
            IPAddress.Loopback, CreateDevnetPolicy().AllowedAddresses, kind == "self" ? TestItem.PublicKeyB : TestItem.PublicKeyC,
            recordSigner: new NodeRecordSigner(crypto));
        using NodesMsg message = new([1], 1, [record]);

        handler.Handle(message);
        Node[] nodes = handler.GetNodes();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Has.Length.EqualTo(kind == "honest" ? 1 : 0));
            Assert.That(crypto.Recoveries, Is.EqualTo(kind is "self" or "off-distance" ? 0 : kind == "honest" ? 1 : 2));
        }
        if (kind == "honest") Assert.That(nodes[0].Id, Is.EqualTo(TestItem.PublicKeyB));
    }

    [TestCase("8.8.8.8", "127.0.0.1", false, 0)]
    [TestCase("127.0.0.1", "127.0.0.1", false, 1)]
    [TestCase("127.0.0.1", "192.0.2.1", false, 0)]
    [TestCase("8.8.8.8", "8.8.4.4", true, 1)]
    public void ShouldFilterRecordByReceiverAndRecordAddress(string receiverIp, string recordIp, bool includeEth2, int expectedCount)
    {
        Node receiver = new(TestItem.PublicKeyA, receiverIp, 30303);
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse(recordIp), includeEth2);
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record);

        using NodesMsg nodes = new([1], 1, [record]);
        handler.Handle(nodes);

        Assert.That(handler.GetNodes(), Has.Length.EqualTo(expectedCount));
    }

    [Test]
    public void ShouldUseRoutableIpv6WhenIpv4IsPrivate()
    {
        Node receiver = new(TestItem.PublicKeyA, "8.8.4.4", 30303);
        IPAddress ip6 = IPAddress.Parse("2606:4700:4700::1111");
        NodeRecord record = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            IPAddress.Parse("10.0.0.1"),
            tcpPort: null,
            udpPort: 30304,
            configureExtras: enr =>
            {
                enr.SetEntry(new Ip6Entry(ip6));
                enr.SetEntry(new Udp6Entry(30305));
            });
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record, IPAddress.IPv6Any);

        using NodesMsg nodes = new([1], 1, [record]);
        handler.Handle(nodes);

        Node[] result = handler.GetNodes();
        Assert.That(result, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0].Host, Is.EqualTo(ip6.ToString()));
            Assert.That(result[0].DiscoveryPort, Is.EqualTo(30305));
        }
    }

    [TestCase("0.0.0.0", "8.8.8.8", 1)]
    [TestCase("0.0.0.0", "2001:4860:4860::8888", 0)]
    [TestCase("::1", "8.8.8.8", 0)]
    [TestCase("::1", "2001:4860:4860::8888", 1)]
    [TestCase("::", "8.8.8.8", 1)]
    [TestCase("::", "2001:4860:4860::8888", 1)]
    public void ShouldFilterRecordsOutsideLocalListenerAddressFamily(string localIp, string recordIp, int expectedCount)
    {
        Node receiver = new(TestItem.PublicKeyA, "8.8.4.4", 30303);
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse(recordIp), includeEth2: true);
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record, IPAddress.Parse(localIp));

        using NodesMsg nodes = new([1], 1, [record]);
        handler.Handle(nodes);

        Assert.That(handler.GetNodes(), Has.Length.EqualTo(expectedCount));
    }

    [Test]
    public void ShouldRejectNodesReadBeforeCompletion()
    {
        Node receiver = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record);

        Assert.That(handler.GetNodes, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task ShouldCollectConcurrentBatchesOnce()
    {
        Node receiver = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeRecord first = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        NodeRecord second = CreateEnr(TestItem.PrivateKeyC, IPAddress.Loopback);
        NodeRecord third = CreateEnr(TestItem.PrivateKeyD, IPAddress.Loopback);
        NodeRecord fourth = CreateEnr(TestItem.PrivateKeyE, IPAddress.Loopback);
        using Distances distances = CreateDistances(receiver, first, second, third, fourth);
        NodesResponseHandler handler = new(receiver, distances, ValueHash256KademliaDistance.Instance,
            IPAddress.Any, CreateDevnetPolicy().AllowedAddresses);

        using NodesMsg firstBatch = new([1], 2, [first, second, first]);
        using NodesMsg secondBatch = new([2], 2, [third, fourth, second]);

        await Task.WhenAll(
            Task.Run(() => handler.Handle(firstBatch)),
            Task.Run(() => handler.Handle(secondBatch)));
        await handler.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Node[] nodes = handler.GetNodes();
        Assert.That(nodes, Has.Length.EqualTo(4));
        AssertUniqueNodeIds(nodes);
    }

    [Test]
    public void ShouldFilterInvalidEndpointsDuplicatesAndUnrequestedDistancesAcrossBatches()
    {
        Node receiver = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        NodeRecord invalid = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        NodeRecord valid = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse("8.8.4.4"));
        NodeRecord duplicate = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse("1.1.1.1"));
        NodeRecord self = CreateEnr(TestItem.PrivateKeyA, IPAddress.Parse("8.8.8.8"));
        using Distances distances = CreateDistances(receiver, valid);
        using NodesResponseHandler handler = new(receiver, distances, ValueHash256KademliaDistance.Instance, IPAddress.Any, PeerAddressPermissions.None);
        using NodesMsg first = new([1], 2, [invalid, valid, self]);
        using NodesMsg second = new([1], 2, [duplicate, valid]);

        handler.Handle(first);
        handler.Handle(second);

        Node[] nodes = handler.GetNodes();
        Assert.That(nodes, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes[0].Id, Is.EqualTo(TestItem.PublicKeyB));
            Assert.That(nodes[0].Host, Is.EqualTo("8.8.4.4"));
            Assert.That(nodes[0].DiscoveryPort, Is.EqualTo(30303));
        }
    }


    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    public void ShouldRetainHighestVerifiedEndpoint(bool alreadyKnown, bool newerFirst)
    {
        Node receiver = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        NodeRecord older = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyB, IPAddress.Parse("8.8.4.4"), enrSequence: 1);
        NodeRecord newer = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyB, IPAddress.Parse("9.9.9.9"), enrSequence: 2);
        IRoutingTable<Node, ValueHash256> routing = Substitute.For<IRoutingTable<Node, ValueHash256>>();
        if (alreadyKnown)
        {
            Node known = new(TestItem.PublicKeyB, "9.9.9.9", 30303);
            known.SetVerifiedEnr(newer);
            routing.TryGet(TestItem.PublicKeyB.Hash.ValueHash256, out _).Returns(call => { call[1] = known; return true; });
        }
        using NodesResponseHandler handler = new(receiver, CreateDistances(receiver, older), ValueHash256KademliaDistance.Instance,
            IPAddress.Any, PeerAddressPermissions.None, TestItem.PublicKeyC, routing);
        using NodesMsg message = new([1], 1, alreadyKnown ? [older] : newerFirst ? [newer, older] : [older, newer]);
        handler.Handle(message);
        Node[] found = handler.GetNodes();
        Assert.That(found, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found[0].Host, Is.EqualTo("9.9.9.9"));
            Assert.That(found[0].Enr!.EnrSequence, Is.EqualTo(2));
            Assert.That(found[0].ValidatedProtocol, Is.Null);
        }
    }

    private static NodeRecord CreateEnr(PrivateKey privateKey, IPAddress ipAddress, bool includeEth2 = false) =>
        TestEnrBuilder.BuildSigned(
            privateKey,
            ipAddress,
            tcpPort: null,
            configureExtras: includeEth2 ? static enr => enr.SetEntry(new TestEth2Entry()) : null);

    private static NodesResponseHandler CreateNodesResponseHandler(Node receiver, NodeRecord record, IPAddress? localIp = null,
        INodeRecordSigner? signer = null) =>
        new(receiver, CreateDistances(receiver, record), ValueHash256KademliaDistance.Instance, localIp ?? IPAddress.Any, allowNonRoutable: CreateDevnetPolicy().AllowedAddresses, recordSigner: signer);

    private static PeerAddressPolicy CreateDevnetPolicy() => new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Loopback),
        new LocalAddressSource(LimboLogs.Instance, () => []));

    private static Distances CreateDistances(Node receiver, params NodeRecord[] records)
    {
        int[] distances = new int[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            distances[i] = GetDistance(receiver, records[i]);
        }

        return new Distances(distances);
    }

    private static int GetDistance(Node receiver, NodeRecord record)
    {
        PublicKey nodeId = record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)!.Decompress();
        return ValueHash256KademliaDistance.Instance.CalculateLogDistance(receiver.Id.Hash.ValueHash256, nodeId.Hash.ValueHash256);
    }

    private static void AssertUniqueNodeIds(Node[] nodes)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            for (int j = i + 1; j < nodes.Length; j++)
            {
                Assert.That(nodes[i].Id.Hash, Is.Not.EqualTo(nodes[j].Id.Hash));
            }
        }
    }
}

internal sealed class CountingEcdsa : IEcdsa
{
    private readonly Ecdsa _inner = new();
    public int Recoveries { get; private set; }
    public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => _inner.Sign(privateKey, in message);
    public PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message)
    {
        Recoveries++;
        return _inner.RecoverPublicKey(signature, in message);
    }
    public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message)
    {
        Recoveries++;
        return _inner.RecoverCompressedPublicKey(signature, in message);
    }
}
