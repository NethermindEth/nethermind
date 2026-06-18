// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5.Handlers;

public class NodesResponseHandlerTests
{
    [TestCase("8.8.8.8", "127.0.0.1", 0)]
    [TestCase("127.0.0.1", "127.0.0.1", 1)]
    [TestCase("127.0.0.1", "192.0.2.1", 0)]
    public void ShouldFilterRecordByReceiverAndRecordAddress(string receiverIp, string recordIp, int expectedCount)
    {
        Node receiver = new(TestItem.PublicKeyA, receiverIp, 30303);
        NodeRecord record = CreateEnr(TestItem.PrivateKeyB, IPAddress.Parse(recordIp));
        NodesResponseHandler handler = CreateNodesResponseHandler(receiver, record);

        using NodesMsg nodes = new([1], 1, [record]);
        handler.Handle(nodes);

        Assert.That(handler.GetNodes(), Has.Length.EqualTo(expectedCount));
    }

    private static NodeRecord CreateEnr(PrivateKey privateKey, IPAddress ipAddress) =>
        TestEnrBuilder.BuildSigned(privateKey, ipAddress, tcpPort: null);

    private static NodesResponseHandler CreateNodesResponseHandler(Node receiver, NodeRecord record)
    {
        PublicKey nodeId = record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)!.Decompress();
        int distance = Hash256KademliaDistance.Instance.CalculateLogDistance(receiver.Id.Hash, nodeId.Hash);
        return new NodesResponseHandler(receiver, new Distances([distance]), Hash256KademliaDistance.Instance, ExecutionLayerDiscv5RecordFilter.Instance);
    }
}
