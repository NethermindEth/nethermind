// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

        peerA.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyB.PublicKey) && HasEnr(node)));
        peerB.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey) && HasEnr(node)));
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

        peerB.Kademlia.Received().AddOrRefresh(Arg.Is<Node>(node => node.Id.Equals(TestItem.PrivateKeyA.PublicKey) && !HasEnr(node)));
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

    [Test]
    public async Task FindNeighbours_ShouldPreferValidatedRecords_WhenBucketHasMoreThanResponseLimit()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA);
        await using TestPeer peerB = CreatePeer(TestItem.PrivateKeyB, endpointB);
        Node nodeB = new(TestItem.PrivateKeyB.PublicKey, endpointB)
        {
            Enr = peerB.NodeRecordProvider.Current
        };

        Node[] bucketNodes = new Node[17];
        for (int i = 0; i < 16; i++)
        {
            bucketNodes[i] = CreateSignedNode(TestItem.PrivateKeys[i], IPEndPoint.Parse($"127.0.0.1:{11000 + i}"));
        }

        Node validatedNode = CreateSignedNode(TestItem.PrivateKeyD, IPEndPoint.Parse("127.0.0.1:12000"));
        validatedNode.ValidatedProtocol = true;
        bucketNodes[^1] = validatedNode;

        using Distances requestedDistances = peerA.Adapter.GetLookupDistances(nodeB, validatedNode.Id);
        for (int i = 0; i < requestedDistances.Count; i++)
        {
            peerB.Kademlia.GetAllAtDistance(requestedDistances[i]).Returns([]);
        }

        peerB.Kademlia.GetAllAtDistance(requestedDistances[0]).Returns(bucketNodes);

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);

        Task<Node[]?> findTask = peerA.Adapter.FindNeighbours(nodeB, validatedNode.Id, cancellationSource.Token);
        await PumpUntilComplete(findTask, peerA, peerB, cancellationSource.Token);
        Node[]? nodes = await findTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB);

        Assert.That(nodes, Is.Not.Null);
        Assert.That(nodes, Has.Length.LessThanOrEqualTo(16));
        Assert.That(nodes, Has.One.Matches<Node>(node => node.Id.Equals(validatedNode.Id)));
    }

    [Test]
    public async Task EndpointCheck_ShouldAdmitValidatedNode_WhenBucketPromotesUnvalidatedReplacement()
    {
        IPEndPoint endpointA = IPEndPoint.Parse("127.0.0.1:10000");
        IPEndPoint endpointB = IPEndPoint.Parse("127.0.0.1:10001");
        IPEndPoint endpointC = IPEndPoint.Parse("127.0.0.1:10002");
        FindKeysAtSameDistance(
            TestItem.PrivateKeyA.PublicKey.Hash,
            out PrivateKey staleKey,
            out PrivateKey replacementKey,
            out PrivateKey liveKey);

        BoundedDistanceKademlia table = new(TestItem.PrivateKeyA.PublicKey.Hash, capacityPerDistance: 1);
        Node staleNode = CreateSignedNode(staleKey, IPEndPoint.Parse("127.0.0.1:11000"));
        Node replacementNode = CreateSignedNode(replacementKey, IPEndPoint.Parse("127.0.0.1:11001"));
        table.AddOrRefresh(staleNode);
        table.AddReplacement(replacementNode);

        await using TestPeer peerA = CreatePeer(TestItem.PrivateKeyA, endpointA, kademlia: table, bucketSize: 1);
        await using TestPeer peerB = CreatePeer(liveKey, endpointB);
        await using TestPeer peerC = CreatePeer(TestItem.PrivateKeyC, endpointC);
        Node nodeA = new(TestItem.PrivateKeyA.PublicKey, endpointA)
        {
            Enr = peerA.NodeRecordProvider.Current
        };

        using CancellationTokenSource cancellationSource = new(10_000);
        Task runA = peerA.Adapter.RunAsync(cancellationSource.Token);
        Task runB = peerB.Adapter.RunAsync(cancellationSource.Token);
        Task runC = peerC.Adapter.RunAsync(cancellationSource.Token);

        Task pingTask = peerB.Adapter.Ping(nodeA, cancellationSource.Token);
        await PumpUntilComplete(pingTask, peerB, peerA, cancellationSource.Token);
        await pingTask;
        await PumpUntil(
            () => table.Contains(liveKey.PublicKey) && !table.Contains(replacementKey.PublicKey),
            peerA,
            peerB,
            cancellationSource.Token);

        Task<Node[]?> findTask = peerC.Adapter.FindNeighbours(nodeA, liveKey.PublicKey, cancellationSource.Token);
        await PumpUntilComplete(findTask, peerC, peerA, cancellationSource.Token);
        Node[]? nodes = await findTask;

        await cancellationSource.CancelAsync();
        await Task.WhenAll(runA, runB, runC);

        Assert.That(nodes, Is.Not.Null);
        Assert.That(nodes, Has.One.Matches<Node>(node => node.Id.Equals(liveKey.PublicKey)));
    }

    private static TestPeer CreatePeer(
        PrivateKey privateKey,
        IPEndPoint endpoint,
        bool includeEndpointInRecord = true,
        ulong enrSequence = 1,
        IKademlia<PublicKey, Node>? kademlia = null,
        int bucketSize = 16)
    {
        IKademlia<PublicKey, Node> table = kademlia ?? Substitute.For<IKademlia<PublicKey, Node>>();
        NettyDiscoveryV5Handler handler = new(new TestLogManager());
        EmbeddedChannel channel = new();
        OutboundDatagramCapture outbound = new();
        channel.Pipeline.AddLast(outbound);
        handler.InitializeChannel(channel);

        TestNodeRecordProvider nodeRecordProvider = new(privateKey, endpoint, includeEndpointInRecord, enrSequence);
        PacketCodec packetCodec = new(
            new InsecureProtectedPrivateKey(privateKey),
            new CryptoRandom(),
            new EthereumEcdsa(0));
        Node currentNode = new(privateKey.PublicKey, endpoint, true);
        KademliaAdapter adapter = new(
            new Lazy<IKademlia<PublicKey, Node>>(table),
            handler,
            packetCodec,
            nodeRecordProvider,
            new DiscoveryConfig(),
            new KademliaConfig<Node> { CurrentNodeId = currentNode, KSize = bucketSize },
            new CryptoRandom(),
            Hash256KademliaDistance.Instance,
            ExecutionLayerDiscv5RecordFilter.Instance,
            LimboLogs.Instance);

        return new TestPeer(adapter, handler, channel, outbound, packetCodec, table, nodeRecordProvider, endpoint);
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

    private static bool HasEnr(Node node) => node.Enr is not null;

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

    private static void FindKeysAtSameDistance(
        Hash256 currentNodeHash,
        out PrivateKey firstKey,
        out PrivateKey secondKey,
        out PrivateKey thirdKey)
    {
        Dictionary<int, List<PrivateKey>> keysByDistance = [];
        for (int i = 0; i < TestItem.PrivateKeys.Length; i++)
        {
            PrivateKey candidate = TestItem.PrivateKeys[i];
            if (candidate.PublicKey.Equals(TestItem.PrivateKeyA.PublicKey) ||
                candidate.PublicKey.Equals(TestItem.PrivateKeyC.PublicKey))
            {
                continue;
            }

            int distance = Hash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash, candidate.PublicKey.Hash);
            if (!keysByDistance.TryGetValue(distance, out List<PrivateKey>? keys))
            {
                keys = [];
                keysByDistance[distance] = keys;
            }

            keys.Add(candidate);
            if (keys.Count == 3)
            {
                firstKey = keys[0];
                secondKey = keys[1];
                thirdKey = keys[2];
                return;
            }
        }

        throw new InvalidOperationException("Could not find three test keys at the same discv5 distance.");
    }

    private static Node CreateSignedNode(PrivateKey privateKey, IPEndPoint endpoint)
        => new(privateKey.PublicKey, endpoint)
        {
            Enr = TestEnrBuilder.BuildSigned(privateKey, endpoint.Address, tcpPort: endpoint.Port, udpPort: endpoint.Port)
        };

    private static void Pump(TestPeer from, TestPeer to)
    {
        while (from.Outbound.TryDequeue(out DatagramPacket? packet))
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

    private sealed class BoundedDistanceKademlia(Hash256 currentNodeHash, int capacityPerDistance) : IKademlia<PublicKey, Node>
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, List<Node>> _nodesByDistance = [];
        private readonly Dictionary<int, List<Node>> _replacementsByDistance = [];

        public event EventHandler<Node>? OnNodeAdded;
        public event EventHandler<Node>? OnNodeRemoved;

        public void AddOrRefresh(Node node)
        {
            int distance = Hash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash, node.Id.Hash);
            bool added = false;
            lock (_lock)
            {
                List<Node> nodes = GetNodes(distance);
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].Id.Equals(node.Id))
                    {
                        nodes[i] = node;
                        return;
                    }
                }

                if (nodes.Count >= capacityPerDistance)
                {
                    return;
                }

                nodes.Add(node);
                added = true;
            }

            if (added)
            {
                OnNodeAdded?.Invoke(this, node);
            }
        }

        public void AddReplacement(Node node)
        {
            int distance = Hash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash, node.Id.Hash);
            lock (_lock)
            {
                GetReplacements(distance).Add(node);
            }
        }

        public void Remove(Node node)
        {
            int distance = Hash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash, node.Id.Hash);
            Node? removed = null;
            lock (_lock)
            {
                if (!_nodesByDistance.TryGetValue(distance, out List<Node>? nodes))
                {
                    return;
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!nodes[i].Id.Equals(node.Id))
                    {
                        continue;
                    }

                    removed = nodes[i];
                    nodes.RemoveAt(i);
                    PromoteReplacement(distance, nodes);
                    break;
                }
            }

            if (removed is not null)
            {
                OnNodeRemoved?.Invoke(this, removed);
            }
        }

        public bool Contains(PublicKey publicKey)
        {
            int distance = Hash256KademliaDistance.Instance.CalculateLogDistance(currentNodeHash, publicKey.Hash);
            lock (_lock)
            {
                if (!_nodesByDistance.TryGetValue(distance, out List<Node>? nodes))
                {
                    return false;
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].Id.Equals(publicKey))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public Task Run(CancellationToken token) => throw new NotSupportedException();

        public Task Bootstrap(CancellationToken token) => throw new NotSupportedException();

        public Task<Node[]> LookupNodesClosest(PublicKey key, CancellationToken token, int? k = null) => throw new NotSupportedException();

        public IAsyncEnumerable<Node> LookupNodes(PublicKey key, CancellationToken token, int? maxResults = null) => throw new NotSupportedException();

        public Node[] GetKNeighbour(PublicKey target, Node? excluding = null, bool excludeSelf = false) => throw new NotSupportedException();

        public Node[] GetAllAtDistance(int distance)
        {
            lock (_lock)
            {
                return _nodesByDistance.TryGetValue(distance, out List<Node>? nodes) ? nodes.ToArray() : [];
            }
        }

        public IEnumerable<Node> IterateNodes()
        {
            Node[] snapshot;
            lock (_lock)
            {
                List<Node> nodes = [];
                foreach (List<Node> bucketNodes in _nodesByDistance.Values)
                {
                    nodes.AddRange(bucketNodes);
                }

                snapshot = nodes.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                yield return snapshot[i];
            }
        }

        private List<Node> GetNodes(int distance)
        {
            if (!_nodesByDistance.TryGetValue(distance, out List<Node>? nodes))
            {
                nodes = [];
                _nodesByDistance[distance] = nodes;
            }

            return nodes;
        }

        private List<Node> GetReplacements(int distance)
        {
            if (!_replacementsByDistance.TryGetValue(distance, out List<Node>? replacements))
            {
                replacements = [];
                _replacementsByDistance[distance] = replacements;
            }

            return replacements;
        }

        private void PromoteReplacement(int distance, List<Node> nodes)
        {
            if (!_replacementsByDistance.TryGetValue(distance, out List<Node>? replacements) || replacements.Count == 0)
            {
                return;
            }

            Node replacement = replacements[0];
            replacements.RemoveAt(0);
            nodes.Add(replacement);
        }
    }

    private sealed record TestPeer(
        KademliaAdapter Adapter,
        NettyDiscoveryV5Handler Handler,
        EmbeddedChannel Channel,
        OutboundDatagramCapture Outbound,
        PacketCodec PacketCodec,
        IKademlia<PublicKey, Node> Kademlia,
        TestNodeRecordProvider NodeRecordProvider,
        IPEndPoint Endpoint) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Adapter.DisposeAsync();
            }
            finally
            {
                try
                {
                    Outbound.ReleaseAll();
                    Channel.FinishAndReleaseAll();
                }
                finally
                {
                    PacketCodec.Dispose();
                }
            }
        }
    }

    /// <summary>Captures outbound datagrams into a thread-safe queue, bypassing the embedded channel's non-thread-safe <c>ChannelOutboundBuffer</c> so packet workers can send concurrently with the test thread's pumping and disposal.</summary>
    private sealed class OutboundDatagramCapture : ChannelHandlerAdapter
    {
        private readonly ConcurrentQueue<DatagramPacket> _queue = new();

        public override Task WriteAsync(IChannelHandlerContext context, object message)
        {
            // discv5 only writes DatagramPackets; anything else would reach the suppressed-flush buffer and re-introduce the race.
            if (message is not DatagramPacket packet)
            {
                throw new NotSupportedException($"Unexpected outbound message type: {message?.GetType()}.");
            }

            _queue.Enqueue(packet);
            return Task.CompletedTask;
        }

        public override void Flush(IChannelHandlerContext context)
        {
            // Datagrams are captured in WriteAsync; there is nothing to flush to the embedded buffer.
        }

        public bool TryDequeue([NotNullWhen(true)] out DatagramPacket? packet) => _queue.TryDequeue(out packet);

        public void ReleaseAll()
        {
            while (_queue.TryDequeue(out DatagramPacket? packet))
            {
                ReferenceCountUtil.Release(packet);
            }
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
