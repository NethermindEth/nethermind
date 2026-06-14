// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Discv5.Packets;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class WireTests
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
        using Distances requestedDistances = peerA.Adapter.GetLookupDistances(nodeB, TestItem.PrivateKeyC.PublicKey);
        for (int i = 0; i < requestedDistances.Count; i++)
        {
            peerB.Kademlia.GetAllAtDistance(requestedDistances[i]).Returns([]);
        }

        peerB.Kademlia.GetAllAtDistance(requestedDistances[0]).Returns([nodeC]);

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task<Node[]?> findTask = peerA.Adapter.FindNeighbours(nodeB, TestItem.PrivateKeyC.PublicKey, cancellationSource.Token);
        await PumpUntilComplete(findTask, peerA, peerB, cancellationSource.Token);
        Node[]? nodes = await findTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        Assert.That(nodes, Is.Not.Null);
        Assert.That(nodes, Has.Length.EqualTo(1));
        Assert.That(nodes![0].Id, Is.EqualTo(TestItem.PrivateKeyC.PublicKey));
        peerA.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyC.PublicKey)));
    }

    private static TestPeer CreatePeer(PrivateKey privateKey, IPEndPoint endpoint, bool includeEndpointInRecord = true)
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        NettyDiscoveryV5Handler handler = new(new TestLogManager());
        EmbeddedChannel channel = new();
        handler.InitializeChannel(channel);

        TestNodeRecordProvider nodeRecordProvider = new(privateKey, endpoint, includeEndpointInRecord);
        KademliaAdapter adapter = new(
            new Lazy<IKademlia<PublicKey, Node>>(kademlia),
            handler,
            new PacketCodec(
                new InsecureProtectedPrivateKey(privateKey),
                nodeRecordProvider,
                new CryptoRandom(),
                new EthereumEcdsa(0)),
            nodeRecordProvider,
            new DiscoveryConfig(),
            new CryptoRandom(),
            Hash256KademliaDistance.Instance,
            ExecutionLayerDiscv5RecordFilter.Instance,
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

    private sealed record TestPeer(
        KademliaAdapter Adapter,
        NettyDiscoveryV5Handler Handler,
        EmbeddedChannel Channel,
        IKademlia<PublicKey, Node> Kademlia,
        TestNodeRecordProvider NodeRecordProvider,
        IPEndPoint Endpoint);

    private sealed class TestNodeRecordProvider(PrivateKey privateKey, IPEndPoint endpoint, bool includeEndpoint) : INodeRecordProvider
    {
        public NodeRecord Current { get; } = includeEndpoint
            ? TestEnrBuilder.BuildSigned(privateKey, endpoint.Address, tcpPort: endpoint.Port, udpPort: endpoint.Port)
            : TestEnrBuilder.BuildSignedWithoutEndpoint(privateKey);
    }
}
