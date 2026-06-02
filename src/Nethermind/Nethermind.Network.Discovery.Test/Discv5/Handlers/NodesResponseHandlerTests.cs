// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5.Handlers;

public class NodesResponseHandlerTests
{
    [Test]
    public void ShouldRejectNonRoutableRecordFromPublicReceiver()
    {
        Node receiver = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
        NodeRecord loopbackRecord = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, loopbackRecord);

        using NodesMsg nodes = new([1], 1, [loopbackRecord]);
        handler.Handle(nodes);

        Assert.That(handler.GetNodes(), Is.Empty);
    }

    [Test]
    public void ShouldAcceptNonRoutableRecordFromNonRoutableReceiver()
    {
        Node receiver = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30303);
        NodeRecord loopbackRecord = CreateEnr(TestItem.PrivateKeyB, IPAddress.Loopback);
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, loopbackRecord);

        using NodesMsg nodes = new([1], 1, [loopbackRecord]);
        handler.Handle(nodes);

        Assert.That(handler.GetNodes(), Has.Length.EqualTo(1));
    }

    [Test]
    public void ShouldRejectSpecialUseRecordFromNonRoutableReceiver()
    {
        Node receiver = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30303);
        NodeRecord documentationRecord = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse("192.0.2.1"));
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, documentationRecord);

        using NodesMsg nodes = new([1], 1, [documentationRecord]);
        handler.Handle(nodes);

        Assert.That(handler.GetNodes(), Is.Empty);
    }

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

    private static NodesResponseHandler CreateNodesResponseHandler(Node receiver, NodeRecord record)
    {
        PublicKey nodeId = record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)!.Decompress();
        int distance = Hash256KademliaDistance.Instance.CalculateLogDistance(receiver.Id.Hash, nodeId.Hash);
        return new NodesResponseHandler(receiver, new Distances([distance]), Hash256KademliaDistance.Instance);
    }
}
