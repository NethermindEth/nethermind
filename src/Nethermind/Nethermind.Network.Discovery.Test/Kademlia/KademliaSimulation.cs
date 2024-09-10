// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NonBlocking;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KademliaSimulation
{

    [Test]
    public async Task TestBootstrap()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(500);

        TestFabric fabric = new TestFabric();
        Random rand = new Random(0);

        ValueHash256 node1Hash = RandomKeccak(rand);
        ValueHash256 node2Hash = RandomKeccak(rand);
        ValueHash256 node3Hash = RandomKeccak(rand);

        Kademlia<TestNode, ValueHash256, ValueHash256> node1 = fabric.CreateNode(node1Hash);
        Kademlia<TestNode, ValueHash256, ValueHash256> node2 = fabric.CreateNode(node2Hash);
        Kademlia<TestNode, ValueHash256, ValueHash256> node3 = fabric.CreateNode(node3Hash);

        node1.AddOrRefresh(new TestNode(node2Hash));
        node2.AddOrRefresh(new TestNode(node3Hash));

        node1.IterateNeighbour(Keccak.Zero).Select(n => n.Hash).ToArray().Should().BeEquivalentTo([node2Hash]);
        node2.IterateNeighbour(Keccak.Zero).Select(n => n.Hash).ToArray().Should().BeEquivalentTo([node3Hash]);
        node3.IterateNeighbour(Keccak.Zero).Select(n => n.Hash).ToArray().Should().BeEmpty();

        await node2.Bootstrap(cts.Token);
        node2.IterateNeighbour(Keccak.Zero).Select(n => n.Hash).ToHashSet().Should().BeEquivalentTo([node3Hash]);

        await node1.Bootstrap(cts.Token);

        node1.IterateNeighbour(Keccak.Zero).Select(n => n.Hash).ToHashSet().Should().BeEquivalentTo([node2Hash, node3Hash]);
        node2.IterateNeighbour(Keccak.Zero).Select(n => n.Hash).ToHashSet().Should().BeEquivalentTo([node1Hash, node3Hash]);
        node3.IterateNeighbour(Keccak.Zero).Select(n => n.Hash).ToHashSet().Should().BeEquivalentTo([node1Hash, node2Hash]);
    }

    [Test]
    public async Task TestLookup()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(500);

        TestFabric fabric = new TestFabric(alpha: 3);
        Random rand = new Random(0);

        ValueHash256 node1Hash = RandomKeccak(rand);
        ValueHash256 node2Hash = RandomKeccak(rand);
        ValueHash256 node3Hash = RandomKeccak(rand);

        Kademlia<TestNode, ValueHash256, ValueHash256> node1 = fabric.CreateNode(node1Hash);
        Kademlia<TestNode, ValueHash256, ValueHash256> node2 = fabric.CreateNode(node2Hash);
        fabric.CreateNode(node3Hash);

        node1.AddOrRefresh(new TestNode(node2Hash));
        node2.AddOrRefresh(new TestNode(node3Hash));

        await fabric.Bootstrap(cts.Token);

        (await node1.LookupValue(node2Hash, cts.Token)).Should().BeEquivalentTo(node2Hash);
        (await node1.LookupValue(node3Hash, cts.Token)).Should().BeEquivalentTo(node3Hash);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SimulateLargeLookupValue(bool useNewLookup)
    {
        int nodeCount = 500;

        TestFabric fabric = new TestFabric(kSize: 20, alpha: 3, useNewLookup: useNewLookup);
        Random rand = new Random(0);
        ValueHash256 mainNodeHash = RandomKeccak(rand);
        Kademlia<TestNode, ValueHash256, ValueHash256> mainNode = fabric.CreateNode(mainNodeHash);

        List<ValueHash256> nodeIds = new();
        for (int i = 0; i < nodeCount; i++)
        {
            ValueHash256 nodeHash = RandomKeccak(rand);
            Kademlia<TestNode, ValueHash256, ValueHash256> kad = fabric.CreateNode(nodeHash);
            kad.AddOrRefresh(new TestNode(mainNodeHash));
            nodeIds.Add(nodeHash);
        }

        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        Stopwatch sw = Stopwatch.StartNew();
        fabric.SimulateLatency = false; // Bootstrap is so slow, latency simulation is disable for it.
        await fabric.Bootstrap(cts.Token);
        TimeSpan bootstrapDuration = sw.Elapsed;
        sw.Restart();
        fabric.SimulateLatency = true;

        fabric.FindValueCount = 0;

        foreach (ValueHash256 node in nodeIds)
        {
            (await mainNode.LookupValue(node, cts.Token)).Should().BeEquivalentTo(node);
        }
        TimeSpan queryDuration = sw.Elapsed;

        Console.Error.WriteLine($"FindValue count per lookup {fabric.FindValueCount / (double)nodeIds.Count}");
        Console.Error.WriteLine($"FindNeighbour count {fabric.FindNeighbourCount}");
        Console.Error.WriteLine($"Bootstrap duration: {bootstrapDuration}");
        Console.Error.WriteLine($"Query duration: {queryDuration}");
    }

    private static ValueHash256 RandomKeccak(Random rand)
    {
        ValueHash256 val = new ValueHash256();
        rand.NextBytes(val.BytesAsSpan);
        return val;
    }

    private class OnlySelfIStore(ValueHash256 self) : IKademlia<TestNode, ValueHash256, ValueHash256>.IStore
    {
        public bool TryGetValue(ValueHash256 hash, out ValueHash256 value)
        {
            if (hash != self)
            {
                value = null;
                return false;
            }

            value = self;
            return true;
        }
    }

    private class ValueHashNodeHashProvider : INodeHashProvider<TestNode, ValueHash256>
    {
        public ValueHash256 GetHash(TestNode node)
        {
            return node.Hash;
        }

        public ValueHash256 GetHash(ValueHash256 key)
        {
            return key;
        }
    }

    private class TestFabric(int kSize = 20, int alpha = 3, bool useNewLookup = true)
    {
        internal long PingCount = 0;
        internal long FindValueCount = 0;
        internal long FindNeighbourCount = 0;

        private int _baseLatency = 5;
        private int _randomLatency = 2;
        public bool SimulateLatency { get; set; } = false;

        internal ConcurrentDictionary<ValueHash256, IKademlia<TestNode, ValueHash256, ValueHash256>> _nodes = new();
        readonly INodeHashProvider<TestNode, ValueHash256> _nodeHashProvider = new ValueHashNodeHashProvider();
        private readonly Random _random = new Random(0);

        private bool TryGetReceiver(TestNode receiverHash, out IKademlia<TestNode, ValueHash256, ValueHash256> messageReceiver)
        {
            return _nodes.TryGetValue(receiverHash.Hash, out messageReceiver!);
        }

        public Kademlia<TestNode, ValueHash256, ValueHash256> CreateNode(ValueHash256 nodeID)
        {
            var nodeIDTestNode = new TestNode(nodeID);
            var kad = new Kademlia<TestNode, ValueHash256, ValueHash256>(
                _nodeHashProvider,
                new OnlySelfIStore(nodeID),
                new SenderForNode(nodeIDTestNode, this),
                new TestLogManager(LogLevel.Error),
                nodeIDTestNode,
                kSize,
                alpha,
                System.TimeSpan.FromHours(1),
                useNewLookup: useNewLookup
            );

            _nodes[nodeID] = kad;

            return kad;
        }

        private class SenderForNode(TestNode sender, TestFabric fabric) : IMessageSender<TestNode, ValueHash256, ValueHash256>
        {
            public async Task Ping(TestNode node, CancellationToken token)
            {
                Interlocked.Increment(ref fabric.PingCount);

                await fabric.DoSimulateLatency(token);
                fabric.Debug($"ping from {sender} to {node}");
                if (fabric.TryGetReceiver(node, out IKademlia<TestNode, ValueHash256, ValueHash256> receiver))
                {
                    await receiver.Ping(sender, token);
                    return;
                }

                throw new Exception($"unknown receiver {node}");
            }

            public async Task<TestNode[]> FindNeighbours(TestNode node, ValueHash256 hash, CancellationToken token)
            {
                Interlocked.Increment(ref fabric.FindNeighbourCount);

                await fabric.DoSimulateLatency(token);
                fabric.Debug($"findn from {sender} to {node}");
                if (fabric.TryGetReceiver(node, out IKademlia<TestNode, ValueHash256, ValueHash256> receiver))
                {
                    return (await receiver.FindNeighbours(sender, hash, token)).Select((node) => new TestNode(node.Hash)).ToArray();
                }

                throw new Exception($"unknown receiver {node}");
            }

            public async Task<FindValueResponse<TestNode, ValueHash256>> FindValue(TestNode node, ValueHash256 hash, CancellationToken token)
            {
                Interlocked.Increment(ref fabric.FindValueCount);

                await fabric.DoSimulateLatency(token);
                fabric.Debug($"finv from {sender} to {node}");
                if (fabric.TryGetReceiver(node, out IKademlia<TestNode, ValueHash256, ValueHash256> receiver))
                {
                    var resp = await receiver.FindValue(sender, hash, token);
                    fabric.Debug($"Got {resp.hasValue} {resp.value} or {resp.neighbours.Length} next");

                    resp = resp with { neighbours = resp.neighbours.Select(node => new TestNode(node.Hash)).ToArray() };
                    return resp;
                }

                throw new Exception($"unknown receiver {node}");
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
            foreach (KeyValuePair<ValueHash256, IKademlia<TestNode, ValueHash256, ValueHash256>> kv in _nodes)
            {
                await kv.Value.Bootstrap(token);
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
    }
}
