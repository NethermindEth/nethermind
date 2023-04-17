// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Benchmarks
{
    public class NodeStatsCtorBenchmarks
    {
        private Node _node;

        [GlobalSetup]
        public void Setup()
        {
            _node = new Node(new PublicKey("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f"), "127.0.0.1", 1234);
        }

        [Benchmark]
        public void Improved()
        {
            throw new NotImplementedException();
        }

        [Benchmark]
        public void Light()
        {
            NodeStatsLight stats = new NodeStatsLight(_node);
        }

        [Benchmark]
        public long LightRep()
        {
            NodeStatsLight stats = new NodeStatsLight(_node);
            return stats.CurrentNodeReputation;
        }
    }
}
