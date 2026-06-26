// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
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
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        peerA.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyB.PublicKey) && node.Enr != null));
        peerB.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey) && node.Enr != null));
    }

    [Test]
    public async Task Ping_Rehandshakes_After_RemoteSessionLost()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
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

        await using TestPeer restartedPeerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
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
    public async Task Ping_Refreshes_RemoteRecord_When_Pong_Advertises_Newer_Sequence()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB, enrSequence: 2);
        NodeRecord staleRecord = TestEnrBuilder.BuildSigned(
            TestItem.PrivateKeyB,
            endpointB.Address,
            tcpPort: endpointB.Port,
            udpPort: endpointB.Port,
            enrSequence: 1);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = staleRecord
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;
        await PumpUntil(
            () => HasReceivedNodeWithEnrSequence(peerA.Kademlia, TestItem.PrivateKeyB.PublicKey, peerB.NodeRecordProvider.Current.EnrSequence),
            peerA,
            peerB,
            cancellationSource.Token);

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        Assert.That(
            HasReceivedNodeWithEnrSequence(peerA.Kademlia, TestItem.PrivateKeyB.PublicKey, peerB.NodeRecordProvider.Current.EnrSequence),
            Is.True);
    }

    [Test]
    public async Task Ping_Completes_With_HandshakeRecord_WithoutEndpoint()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA, includeEndpointInRecord: false);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerA.Adapter.Ping(nodeB, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerA, peerB, cancellationSource.Token);
        await pingTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        peerB.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey) && node.Enr == null));
    }

    [Test]
    public async Task InboundPing_Starts_EndpointCheck_PingBack()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
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
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        await using TestPeer peerC = CreatePeer(TestItem.PrivateKeyC, endpointC);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };
        Node nodeC = new(TestItem.PrivateKeyC.PublicKey, endpointC)
        {
            Enr = peerC.NodeRecordProvider.Current
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

    private static TestPeer CreatePeer(PrivateKey privateKey, IPEndPoint endpoint, bool includeEndpointInRecord = true, ulong enrSequence = 1)
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        NettyDiscoveryV5Handler handler = new(new TestLogManager());
        EmbeddedChannel channel = new();
        handler.InitializeChannel(channel);

        TestNodeRecordProvider nodeRecordProvider = new(privateKey, endpoint, includeEndpointInRecord, enrSequence);
        PacketCodec packetCodec = new(
            new InsecureProtectedPrivateKey(privateKey),
            new CryptoRandom(),
            new EthereumEcdsa(0));
        Node currentNode = new(privateKey.PublicKey, endpoint, true);
        KademliaAdapter adapter = new(
            new Lazy<IKademlia<PublicKey, Node>>(kademlia),
            handler,
            packetCodec,
            nodeRecordProvider,
            new DiscoveryConfig(),
            new KademliaConfig<Node> { CurrentNodeId = currentNode },
            new CryptoRandom(),
            Hash256KademliaDistance.Instance,
            ExecutionLayerDiscv5RecordFilter.Instance,
            LimboLogs.Instance);

        return new TestPeer(adapter, handler, channel, packetCodec, kademlia, nodeRecordProvider, endpoint);
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

    private static bool HasEnrSequence(Node node, ulong sequence)
    {
        if (node.Enr is null)
        {
            return false;
        }

        try
        {
            return node.Enr.EnrSequence == sequence;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasReceivedNodeWithEnrSequence(IKademlia<PublicKey, Node> kademlia, PublicKey publicKey, ulong sequence)
    {
        foreach (NSubstitute.Core.ICall call in kademlia.ReceivedCalls())
        {
            if (call.GetMethodInfo().Name == nameof(IKademlia<PublicKey, Node>.AddOrRefresh) &&
                call.GetArguments()[0] is Node node &&
                node.Id.Equals(publicKey) &&
                HasEnrSequence(node, sequence))
            {
                return true;
            }
        }

        return false;
    }

    private static void Pump(TestPeer from, TestPeer to)
    {
        while (from.Channel.ReadOutbound<DatagramPacket>() is { } packet)
        {
            try
            {
                byte[] data = packet.Content.ReadAllBytesAsArray();
                IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
                to.Handler.ChannelRead(context, new DatagramPacket(Unpooled.WrappedBuffer(data), from.Endpoint, to.Endpoint));
            }
            finally
            {
                ReferenceCountUtil.Release(packet);
            }
        }
    }

    private sealed record TestPeer(
        KademliaAdapter Adapter,
        NettyDiscoveryV5Handler Handler,
        EmbeddedChannel Channel,
        PacketCodec PacketCodec,
        IKademlia<PublicKey, Node> Kademlia,
        TestNodeRecordProvider NodeRecordProvider,
        IPEndPoint Endpoint) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Adapter.DisposeAsync();
            await Channel.CloseAsync();
            Channel.FinishAndReleaseAll();
            PacketCodec.Dispose();
        }
    }

    private sealed class TestNodeRecordProvider(PrivateKey privateKey, IPEndPoint endpoint, bool includeEndpoint, ulong enrSequence) : INodeRecordProvider
    {
        public NodeRecord Current { get; } = includeEndpoint
            ? TestEnrBuilder.BuildSigned(privateKey, endpoint.Address, tcpPort: endpoint.Port, udpPort: endpoint.Port, enrSequence: enrSequence)
            : TestEnrBuilder.BuildSignedWithoutEndpoint(privateKey, enrSequence);

        public ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default) => new(Current);
    }
}
