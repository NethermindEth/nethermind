// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
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

        TestFabricMessageSender fabric = new TestFabricMessageSender();
        Random rand = new Random(0);

        ValueHash256 node1Hash = RandomKeccak(rand);
        ValueHash256 node2Hash = RandomKeccak(rand);
        ValueHash256 node3Hash = RandomKeccak(rand);

        Kademlia<ValueHash256, ValueHash256> node1 = fabric.CreateNode(node1Hash);
        Kademlia<ValueHash256, ValueHash256> node2 = fabric.CreateNode(node2Hash);
        Kademlia<ValueHash256, ValueHash256> node3 = fabric.CreateNode(node3Hash);

        node1.SeedNode(node2Hash);
        node2.SeedNode(node3Hash);

        node1.IterateNeighbour(Keccak.Zero).ToArray().Should().BeEquivalentTo([node2Hash]);
        node2.IterateNeighbour(Keccak.Zero).ToArray().Should().BeEquivalentTo([node3Hash]);
        node3.IterateNeighbour(Keccak.Zero).ToArray().Should().BeEmpty();

        await node2.Bootstrap(cts.Token);
        node2.IterateNeighbour(Keccak.Zero).ToHashSet().Should().BeEquivalentTo([node3Hash]);

        await node1.Bootstrap(cts.Token);

        node1.IterateNeighbour(Keccak.Zero).ToHashSet().Should().BeEquivalentTo([node2Hash, node3Hash]);
        node2.IterateNeighbour(Keccak.Zero).ToHashSet().Should().BeEquivalentTo([node1Hash, node3Hash]);
        node3.IterateNeighbour(Keccak.Zero).ToHashSet().Should().BeEquivalentTo([node1Hash, node2Hash]);
    }

    [Test]
    public async Task TestLookup()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(500);

        TestFabricMessageSender fabric = new TestFabricMessageSender();
        Random rand = new Random(0);

        ValueHash256 node1Hash = RandomKeccak(rand);
        ValueHash256 node2Hash = RandomKeccak(rand);
        ValueHash256 node3Hash = RandomKeccak(rand);

        Kademlia<ValueHash256, ValueHash256> node1 = fabric.CreateNode(node1Hash);
        Kademlia<ValueHash256, ValueHash256> node2 = fabric.CreateNode(node2Hash);
        fabric.CreateNode(node3Hash);

        node1.SeedNode(node2Hash);
        node2.SeedNode(node3Hash);

        await fabric.Bootstrap(cts.Token);

        Console.Out.WriteLine("Lookup =======");

        (await node1.LookupValue(node2Hash, cts.Token)).Should().Be(node2Hash);
        (await node1.LookupValue(node3Hash, cts.Token)).Should().Be(node3Hash);
    }

    [Test]
    public async Task SimulateLargeLookupValue()
    {
        TestFabricMessageSender fabric = new TestFabricMessageSender(kSize: 20, alpha: 3);
        Random rand = new Random(0);
        ValueHash256 mainNodeHash = RandomKeccak(rand);
        Kademlia<ValueHash256, ValueHash256> mainNode = fabric.CreateNode(mainNodeHash);

        List<ValueHash256> nodeIds = new();
        for (int i = 0; i < 500; i++)
        {
            ValueHash256 nodeHash = RandomKeccak(rand);
            Kademlia<ValueHash256, ValueHash256> kad = fabric.CreateNode(nodeHash);
            kad.SeedNode(mainNodeHash);
            nodeIds.Add(nodeHash);
        }

        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(10000);

        await fabric.Bootstrap(cts.Token);

        // fabric.IsDebugging = true;
        // mainNode.Debug = true;
        // var problematic = new ValueHash256("0x82bf3eb6be6c2d15511b0dc6c68c97bad52b834b11656c6104af44123e565a3d");

        fabric.FindValueCount = 0;

        foreach (ValueHash256 node in nodeIds)
        {
            (await mainNode.LookupValue(node, cts.Token)).Should().Be(node);
        }

        Console.Error.WriteLine($"FindValue count per lookup {fabric.FindValueCount / (double)nodeIds.Count}");
    }

    private static ValueHash256 RandomKeccak(Random rand)
    {
        ValueHash256 val = new ValueHash256();
        rand.NextBytes(val.BytesAsSpan);
        return val;
    }

    private class OnlySelfIStore(ValueHash256 self) : IKademlia<ValueHash256, ValueHash256>.IStore
    {
        public bool TryGetValue(ValueHash256 hash, out ValueHash256 value)
        {
            if (hash != self)
            {
                value = Keccak.Zero;
                return false;
            }

            value = self;
            return true;
        }
    }

    private class TestFabricMessageSender(int kSize = 20, int alpha = 3)
    {
        internal long PingCount = 0;
        internal long FindValueCount = 0;
        internal long FindNeighbourCount = 0;

        private ConcurrentDictionary<ValueHash256, IKademlia<ValueHash256, ValueHash256>> _nodes = new();
        readonly IDistanceCalculator<ValueHash256> _distanceCalculator = new Hash256DistanceCalculator();

        private bool TryGetReceiver(ValueHash256 receiverHash, out IKademlia<ValueHash256, ValueHash256> messageReceiver)
        {
            return _nodes.TryGetValue(receiverHash, out messageReceiver!);
        }

        public Kademlia<ValueHash256, ValueHash256> CreateNode(ValueHash256 nodeID)
        {
            var kad = new Kademlia<ValueHash256, ValueHash256>(
                _distanceCalculator,
                new OnlySelfIStore(nodeID),
                new SenderForNode(nodeID, this),
                nodeID,
                kSize,
                alpha,
                System.TimeSpan.FromHours(1)
            );

            _nodes[nodeID] = kad;

            return kad;
        }

        private class SenderForNode(ValueHash256 sender, TestFabricMessageSender fabric) : IMessageSender<ValueHash256, ValueHash256>
        {
            public Task Ping(ValueHash256 receiverHash, CancellationToken token)
            {
                Interlocked.Increment(ref fabric.PingCount);
                fabric.Debug($"ping from {sender} to {receiverHash}");
                if (fabric.TryGetReceiver(receiverHash, out IKademlia<ValueHash256, ValueHash256> receiver))
                {
                    return receiver.Ping(sender, token);
                }

                throw new Exception($"unknown receiver {receiverHash}");
            }

            public async Task<ValueHash256[]> FindNeighbours(ValueHash256 receiverHash, ValueHash256 hash, CancellationToken token)
            {
                Interlocked.Increment(ref fabric.FindNeighbourCount);
                fabric.Debug($"findn from {sender} to {receiverHash}");
                if (fabric.TryGetReceiver(receiverHash, out IKademlia<ValueHash256, ValueHash256> receiver))
                {
                    return await receiver.FindNeighbours(sender, hash, token);
                }

                throw new Exception($"unknown receiver {receiverHash}");
            }

            public async Task<FindValueResponse<ValueHash256, ValueHash256>> FindValue(ValueHash256 receiverHash, ValueHash256 hash, CancellationToken token)
            {
                Interlocked.Increment(ref fabric.FindValueCount);
                fabric.Debug($"finv from {sender} to {receiverHash}");
                if (fabric.TryGetReceiver(receiverHash, out IKademlia<ValueHash256, ValueHash256> receiver))
                {
                    var resp = await receiver.FindValue(sender, hash, token);
                    fabric.Debug($"Got {resp.hasValue} {resp.value} or {resp.neighbours.Length} next");
                    return resp;
                }

                throw new Exception($"unknown receiver {receiverHash}");
            }
        }

        private void Debug(string debugString)
        {
            if (!IsDebugging) return;
            Console.Error.WriteLine(debugString);
        }

        public bool IsDebugging { get; set; }

        public async Task Bootstrap(CancellationToken token)
        {
            foreach (KeyValuePair<ValueHash256, IKademlia<ValueHash256, ValueHash256>> kv in _nodes)
            {
                await kv.Value.Bootstrap(token);
            }
            // var allNodes = _nodes.Select(kv => kv.Value).ToList();
            // await Task.WhenAll(allNodes.Select(n => n.Bootstrap(token)));
        }
    }
}
