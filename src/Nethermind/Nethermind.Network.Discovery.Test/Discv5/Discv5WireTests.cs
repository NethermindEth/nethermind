// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class Discv5WireTests
{
    [Test]
    public async Task Ping_Completes_After_WhoAreYou_Handshake()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current.EnrString
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        peerA.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyB.PublicKey) && !string.IsNullOrEmpty(node.Enr)));
        peerB.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey) && !string.IsNullOrEmpty(node.Enr)));
    }

    [Test]
    public async Task Ping_Rehandshakes_After_RemoteSessionLost()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current.EnrString
        };

        using CancellationTokenSource cancellationSourceA = new(10_000);
        using CancellationTokenSource cancellationSourceB = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSourceA.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSourceB.Token);

        Task firstPing = peerA.Adapter.Ping(nodeB, cancellationSourceA.Token);
        await PumpUntilComplete(firstPing, peerA, peerB, cancellationSourceA.Token);
        await firstPing;

        await cancellationSourceB.CancelAsync();
        await runB;

        TestPeer restartedPeerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        using CancellationTokenSource cancellationSourceRestartedB = new(10_000);
        Task runRestartedB = restartedPeerB.Adapter.RunAsync(cancellationSourceRestartedB.Token);

        Task secondPing = peerA.Adapter.Ping(nodeB, cancellationSourceA.Token);
        await PumpUntilComplete(secondPing, peerA, restartedPeerB, cancellationSourceA.Token);
        await secondPing;

        await cancellationSourceA.CancelAsync();
        await cancellationSourceRestartedB.CancelAsync();
        await Task.WhenAll(runA, runRestartedB);
    }

    [Test]
    public async Task Ping_Completes_With_HandshakeRecord_WithoutEndpoint()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA, includeEndpointInRecord: false);
        TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current.EnrString
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        peerB.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey) && string.IsNullOrEmpty(node.Enr)));
    }

    [Test]
    public async Task InboundPing_Starts_EndpointCheck_PingBack()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current.EnrString
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;

        await PumpUntil(
            () => peerB.Kademlia.ReceivedCallsMatching(
                kademlia => kademlia.AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey))),
                requiredNumberOfCalls: 2,
                maxNumberOfCalls: int.MaxValue),
            peerA,
            peerB,
            cancellationSource.Token);

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);
    }

    [Test]
    public async Task FindNeighbours_Returns_Records_At_Requested_Distance()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        IPEndPoint endpointC = IPEndPoint.Parse("127.0.0.1:10002");
        TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        TestPeer peerC = CreatePeer(TestItem.PrivateKeyC, endpointC);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current.EnrString
        };
        Node nodeC = new(TestItem.PrivateKeyC.PublicKey, endpointC)
        {
            Enr = peerC.NodeRecordProvider.Current.EnrString
        };
        int[] requestedDistances = GetLookupDistances(nodeB, TestItem.PrivateKeyC.PublicKey);
        for (int i = 0; i < requestedDistances.Length; i++)
        {
            peerB.Kademlia.GetAllAtDistance(requestedDistances[i]).Returns([]);
        }

        peerB.Kademlia.GetAllAtDistance(requestedDistances[0]).Returns([nodeC]);

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task<Node[]> findTask = peerA.Adapter.FindNeighbours(nodeB, TestItem.PrivateKeyC.PublicKey, cancellationSource.Token);
        await PumpUntilComplete(findTask, peerA, peerB, cancellationSource.Token);
        Node[] nodes = await findTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        Assert.That(nodes, Has.Length.EqualTo(1));
        Assert.That(nodes[0].Id, Is.EqualTo(TestItem.PrivateKeyC.PublicKey));
        peerA.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyC.PublicKey)));
    }

    private static TestPeer CreatePeer(PrivateKey privateKey, IPEndPoint endpoint, bool includeEndpointInRecord = true)
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        NettyDiscoveryV5Handler handler = new(new TestLogManager());
        EmbeddedChannel channel = new();
        handler.InitializeChannel(channel);

        TestNodeRecordProvider nodeRecordProvider = new(privateKey, endpoint, includeEndpointInRecord);
        Discv5KademliaAdapter adapter = new(
            new Lazy<IKademlia<PublicKey, Node>>(kademlia),
            handler,
            new Discv5PacketCodec(
                new InsecureProtectedPrivateKey(privateKey),
                nodeRecordProvider,
                new CryptoRandom(),
                new EthereumEcdsa(0)),
            nodeRecordProvider,
            new DiscoveryConfig(),
            new CryptoRandom(),
            LimboLogs.Instance);

        return new TestPeer(adapter, handler, channel, kademlia, nodeRecordProvider, endpoint);
    }

    private static async Task PumpUntilComplete(Task task, TestPeer peerA, TestPeer peerB, CancellationToken token)
    {
        while (!task.IsCompleted)
        {
            Pump(peerA, peerB);
            Pump(peerB, peerA);
            await Task.Delay(10, token);
        }

        Pump(peerA, peerB);
        Pump(peerB, peerA);
    }

    private static async Task PumpUntil(Func<bool> condition, TestPeer peerA, TestPeer peerB, CancellationToken token)
    {
        while (!condition())
        {
            Pump(peerA, peerB);
            Pump(peerB, peerA);
            await Task.Delay(10, token);
        }

        Pump(peerA, peerB);
        Pump(peerB, peerA);
    }

    private static void Pump(TestPeer from, TestPeer to)
    {
        while (from.Channel.ReadOutbound<DatagramPacket>() is { } packet)
        {
            byte[] data = packet.Content.ReadAllBytesAsArray();
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            to.Handler.ChannelRead(context, new DatagramPacket(Unpooled.WrappedBuffer(data), from.Endpoint, to.Endpoint));
        }
    }

    private static int[] GetLookupDistances(Node receiver, PublicKey target)
    {
        KademliaHash receiverHash = KademliaHash.FromBytes(receiver.Id.Hash.Bytes);
        KademliaHash targetHash = KademliaHash.FromBytes(target.Hash.Bytes);
        int distance = Hash256XorUtils.CalculateLogDistance(receiverHash, targetHash);

        List<int> distances = [distance];
        if (distance > 0)
        {
            distances.Add(distance - 1);
        }

        if (distance < Hash256XorUtils.MaxDistance)
        {
            distances.Add(distance + 1);
        }

        return [.. distances];
    }

    private sealed record TestPeer(
        Discv5KademliaAdapter Adapter,
        NettyDiscoveryV5Handler Handler,
        EmbeddedChannel Channel,
        IKademlia<PublicKey, Node> Kademlia,
        TestNodeRecordProvider NodeRecordProvider,
        IPEndPoint Endpoint);

    private sealed class TestNodeRecordProvider : INodeRecordProvider
    {
        public TestNodeRecordProvider(PrivateKey privateKey, IPEndPoint endpoint, bool includeEndpoint)
        {
            NodeRecord nodeRecord = new();
            nodeRecord.SetEntry(IdEntry.Instance);
            if (includeEndpoint)
            {
                nodeRecord.SetEntry(new IpEntry(endpoint.Address));
                nodeRecord.SetEntry(new TcpEntry(endpoint.Port));
                nodeRecord.SetEntry(new UdpEntry(endpoint.Port));
            }
            nodeRecord.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
            nodeRecord.EnrSequence = 1;
            new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(nodeRecord);
            Current = nodeRecord;
        }

        public NodeRecord Current { get; }
    }
}
