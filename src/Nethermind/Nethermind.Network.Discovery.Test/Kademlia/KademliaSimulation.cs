// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using NonBlocking;
using NUnit.Framework;
using TestKademlia = Nethermind.Kademlia.Kademlia<Nethermind.Core.Crypto.ValueHash256, Nethermind.Network.Discovery.Test.Kademlia.KademliaSimulation.TestNode, Nethermind.Core.Crypto.Hash256>;

namespace Nethermind.Network.Discovery.Test.Kademlia;

[TestFixture(1, 0)]
[TestFixture(1, 4)]
[TestFixture(3, 0)]
[TestFixture(3, 4)]
public class KademliaSimulation
{
    private readonly KademliaConfig<ValueHash256> _config;

    public KademliaSimulation(int alpha, int beta) =>
        _config = new KademliaConfig<ValueHash256>
        {
            KSize = 20,
            Alpha = alpha,
            Beta = beta,
        };

    private TestFabric CreateFabric() => new(_config);

    [Test]
    public async Task TestBootstrap()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(500);

        TestFabric fabric = CreateFabric();
        Random rand = new(0);

        ValueHash256 node1Hash = RandomKeccak(rand);
        ValueHash256 node2Hash = RandomKeccak(rand);
        ValueHash256 node3Hash = RandomKeccak(rand);

        TestKademlia node1 = fabric.CreateNode(node1Hash);
        TestKademlia node2 = fabric.CreateNode(node2Hash);
        TestKademlia node3 = fabric.CreateNode(node3Hash);

        Assert.That(node1.GetKNeighbour(Keccak.Zero, null).Select(n => n.Hash).ToArray(), Is.EquivalentTo(new[] { node1Hash }));

        node1.AddOrRefresh(new TestNode(node2Hash));
        node2.AddOrRefresh(new TestNode(node3Hash));

        Assert.That(node1.GetKNeighbour(Keccak.Zero, null).Select(n => n.Hash).ToArray(), Is.EquivalentTo(new[] { node1Hash, node2Hash }));
        Assert.That(node2.GetKNeighbour(Keccak.Zero, null).Select(n => n.Hash).ToArray(), Is.EquivalentTo(new[] { node2Hash, node3Hash }));
        Assert.That(node3.GetKNeighbour(Keccak.Zero, null).Select(n => n.Hash).ToArray(), Is.EquivalentTo(new[] { node3Hash }));

        // await node2.Bootstrap(cts.Token);
        // node2.GetKNeighbour(Keccak.Zero, null).Select(n => n.Hash).ToHashSet().Should().BeEquivalentTo([node2Hash, node3Hash]);

        await node1.Bootstrap(cts.Token);

        Assert.That(node1.GetKNeighbour(Keccak.Zero, null).Select(n => n.Hash).ToHashSet(), Is.EquivalentTo(new[] { node1Hash, node2Hash, node3Hash }));
        Assert.That(node2.GetKNeighbour(Keccak.Zero, null).Select(n => n.Hash).ToHashSet(), Is.EquivalentTo(new[] { node1Hash, node2Hash, node3Hash }));
        // node3.GetKNeighbour(Keccak.Zero, null).Select(n => n.Hash).ToHashSet().Should().BeEquivalentTo([node1Hash, node2Hash, node3Hash]);
    }

    [Test]
    public async Task TestKNearestNeighbour()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(500);

        TestFabric fabric = CreateFabric();
        Random rand = new(0);

        ValueHash256 node1Hash = RandomKeccak(rand);
        ValueHash256 node2Hash = RandomKeccak(rand);
        ValueHash256 node3Hash = RandomKeccak(rand);

        TestKademlia node1 = fabric.CreateNode(node1Hash);

        Assert.That(
            (await node1.LookupNodesClosest(node1Hash, cts.Token))
            .Select(n => n.Hash)
            .ToArray(),
            Is.EquivalentTo(new[] { node1Hash }));

        TestKademlia node2 = fabric.CreateNode(node2Hash);
        fabric.CreateNode(node3Hash);

        node1.AddOrRefresh(new TestNode(node2Hash));
        node2.AddOrRefresh(new TestNode(node3Hash));

        await fabric.Bootstrap(cts.Token);

        Assert.That(
            (await node1.LookupNodesClosest(node2Hash, cts.Token))
            .Select(n => n.Hash)
            .ToArray(),
            Is.EquivalentTo(new[] { node1Hash, node2Hash, node3Hash }));

        Assert.That((await node1.LookupNodesClosest(node3Hash, cts.Token, 1)).First().Hash, Is.EqualTo(node3Hash));
    }

    [Test]
    public async Task SimulateLargeKNearestNeighbour()
    {
        int nodeCount = 500;

        TestFabric fabric = CreateFabric();
        Random rand = new(0);
        ValueHash256 mainNodeHash = RandomKeccak(rand);
        TestKademlia mainNode = fabric.CreateNode(mainNodeHash);

        List<ValueHash256> nodeIds = [];
        for (int i = 0; i < nodeCount; i++)
        {
            ValueHash256 nodeHash = RandomKeccak(rand);
            TestKademlia kad = fabric.CreateNode(nodeHash);
            kad.AddOrRefresh(new TestNode(mainNodeHash));
            nodeIds.Add(nodeHash);
        }

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        Stopwatch sw = Stopwatch.StartNew();
        // This test is really slow. Slower than find value which can short circuit once it find the value.
        fabric.SimulateLatency = false;
        await fabric.Bootstrap(cts.Token);
        TimeSpan bootstrapDuration = sw.Elapsed;
        sw.Restart();
        fabric.FindNeighbourCount = 0;

        int closestKCount = 0;
        int missedCount = 0;

        foreach (ValueHash256 targetNode in nodeIds)
        {
            TestNode[] nodesClosest = await mainNode.LookupNodesClosest(targetNode, cts.Token);
            HashSet<ValueHash256> expectedNodeClosestK = nodeIds
                .Order(Comparer<ValueHash256>.Create((n1, n2) => Hash256KademliaDistance.Instance.Compare(ToHash(n1), ToHash(n2), ToHash(targetNode))))
                .Take(_config.KSize)
                .ToHashSet();

            Assert.That(nodesClosest, Has.Length.EqualTo(_config.KSize));

            foreach (TestNode node in nodesClosest)
            {
                if (expectedNodeClosestK.Contains(node.Hash))
                {
                    closestKCount++;
                }
                else
                {
                    missedCount++;
                }
            }
        }
        TimeSpan queryDuration = sw.Elapsed;
        double totalNodesReturned = nodeIds.Count * _config.KSize;

        Assert.That(closestKCount / totalNodesReturned, Is.GreaterThan(0.95));

        TestContext.Out.WriteLine($"Closest K ratio {closestKCount / totalNodesReturned}");
        TestContext.Out.WriteLine($"Missed ratio {missedCount / totalNodesReturned}");
        TestContext.Out.WriteLine($"FindNeighbour count per lookup {fabric.FindNeighbourCount / (double)nodeIds.Count}");
        TestContext.Out.WriteLine($"FindNeighbour count {fabric.FindNeighbourCount}");
        TestContext.Out.WriteLine($"Bootstrap duration: {bootstrapDuration}");
        TestContext.Out.WriteLine($"Query duration: {queryDuration}");
    }

    private static ValueHash256 RandomKeccak(Random rand)
    {
        ValueHash256 val = new();
        rand.NextBytes(val.BytesAsSpan);
        return val;
    }

    private static Hash256 ToHash(ValueHash256 hash) => ValueHashKeyOperator<TestNode>.ToHash(hash);

    private class TestFabric(KademliaConfig<ValueHash256> config)
    {
        internal long PingCount = 0;
        internal long FindNeighbourCount = 0;

        private int _baseLatency = 5;
        private int _randomLatency = 2;
        public bool SimulateLatency { get; set; } = false;

        internal ConcurrentDictionary<ValueHash256, ILifetimeScope> _nodes = new();
        private readonly ValueHashKeyOperator<TestNode> _nodeHashProvider = new(static node => node.Hash);
        private readonly Random _random = new(0);

        private bool TryGetReceiver(TestNode receiverHash, out ReceiverForNode contentKademliaMessageReceiver)
        {
            contentKademliaMessageReceiver = null!;
            if (_nodes.TryGetValue(receiverHash.Hash, out ILifetimeScope? container))
            {
                contentKademliaMessageReceiver = container!.Resolve<ReceiverForNode>();
                return true;
            }

            return false;
        }

        public TestKademlia CreateNode(ValueHash256 nodeID)
        {
            TestNode nodeIDTestNode = new(nodeID);

            ContainerBuilder builder = new();
            builder
                .AddModule(new KademliaModule<ValueHash256, TestNode, Hash256>())
                .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Error))
                .AddSingleton<ITimestamper>(new ManualTimestamper(new DateTime(2025, 5, 13, 21, 0, 0, DateTimeKind.Utc)))
                .AddSingleton<IKademliaDistance<Hash256>>(Hash256KademliaDistance.Instance)
                .AddSingleton<IKeyOperator<ValueHash256, TestNode, Hash256>>(_nodeHashProvider)
                .AddSingleton(new KademliaConfig<TestNode>
                {
                    CurrentNodeId = nodeIDTestNode,
                    KSize = config.KSize,
                    Alpha = config.Alpha,
                    Beta = config.Beta,
                    RefreshInterval = TimeSpan.FromHours(1),
                })
                .AddSingleton<IKademliaMessageSender<ValueHash256, TestNode>>(new SenderForNode(nodeIDTestNode, this))
                .AddSingleton<ReceiverForNode>()
                .AddSingleton<TestKademlia>();

            IContainer container = builder.Build();

            _nodes[nodeID] = container;

            return container.Resolve<TestKademlia>();
        }

        private class SenderForNode(TestNode sender, TestFabric fabric) : IKademliaMessageSender<ValueHash256, TestNode>
        {
            public async Task<bool> Ping(TestNode node, CancellationToken token)
            {
                Interlocked.Increment(ref fabric.PingCount);

                await fabric.DoSimulateLatency(token);
                fabric.Debug($"ping from {sender} to {node}");
                if (fabric.TryGetReceiver(node, out ReceiverForNode receiver))
                {
                    await receiver.Ping(sender, token);
                    return true;
                }

                throw new Exception($"unknown receiver {node}");
            }

            public async Task<TestNode[]?> FindNeighbours(TestNode node, ValueHash256 hash, CancellationToken token)
            {
                Interlocked.Increment(ref fabric.FindNeighbourCount);

                await fabric.DoSimulateLatency(token);
                fabric.Debug($"find neighbours from {sender} to {node}");
                if (fabric.TryGetReceiver(node, out ReceiverForNode receiver))
                {
                    TestNode[]? neighbours = await receiver.FindNeighbours(sender, hash, token);
                    return neighbours?.Select((node) => new TestNode(node.Hash)).ToArray();
                }

                throw new Exception($"unknown receiver {node}");
            }
        }

        private class ReceiverForNode(IKademlia<ValueHash256, TestNode> kademlia, INodeHealthTracker<TestNode> nodeHealthTracker)
        {
            public Task<bool> Ping(TestNode node, CancellationToken token)
            {
                nodeHealthTracker.OnIncomingMessageFrom(node);
                return Task.FromResult(true);
            }

            public Task<TestNode[]?> FindNeighbours(TestNode node, ValueHash256 hash, CancellationToken token)
            {
                nodeHealthTracker.OnIncomingMessageFrom(node);
                return Task.FromResult<TestNode[]?>(kademlia.GetKNeighbour(hash, node));
            }
        }

        private Task DoSimulateLatency(CancellationToken token)
        {
            if (!SimulateLatency) return Task.CompletedTask;
            return Task.Delay(_baseLatency + _random.Next(_randomLatency), token);
        }

        private void Debug(string debugString)
        {
            if (!IsDebugging) return;
            Console.Error.WriteLine(debugString);
        }

        public bool IsDebugging { get; set; }

        public async Task Bootstrap(CancellationToken token)
        {
            foreach (KeyValuePair<ValueHash256, ILifetimeScope> kv in _nodes)
            {
                await kv.Value.Resolve<IKademlia<ValueHash256, TestNode>>().Bootstrap(token);
            }
        }
    }

    /// <summary>
    /// Class representing node in testing. Deliberately used where the hash does not match to make sure that
    /// Kademlia code assume so.
    /// </summary>
    /// <param name="hash"></param>
    internal class TestNode(ValueHash256 hash)
    {
        public ValueHash256 Hash => hash;

        public override string ToString() => Hash.ToString();
    }
}
