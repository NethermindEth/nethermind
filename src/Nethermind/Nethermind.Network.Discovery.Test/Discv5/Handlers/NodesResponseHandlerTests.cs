// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
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
        NodesResponseHandler handler = new(receiver, distances, Hash256KademliaDistance.Instance, ExecutionLayerDiscv5RecordFilter.Instance);

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

    private static NodeRecord CreateEnr(PrivateKey privateKey, IPAddress ipAddress) =>
        TestEnrBuilder.BuildSigned(privateKey, ipAddress, tcpPort: null);

    private static NodesResponseHandler CreateNodesResponseHandler(Node receiver, NodeRecord record) =>
        new(receiver, CreateDistances(receiver, record), Hash256KademliaDistance.Instance, ExecutionLayerDiscv5RecordFilter.Instance);

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
        return Hash256KademliaDistance.Instance.CalculateLogDistance(receiver.Id.Hash, nodeId.Hash);
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
