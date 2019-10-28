﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class NodeStatsCtorBenchmarks
    {
        private ILogManager _logManager;
        private Node _node;
        private IStatsConfig _statsConfig;
        
        [GlobalSetup]
        public void Setup()
        {
            _node = new Node(new PublicKey("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f"), "127.0.0.1", 1234);
            _statsConfig = new StatsConfig();
            _statsConfig.CaptureNodeStatsEventHistory = true;
            _statsConfig.CaptureNodeLatencyStatsEventHistory = true;
            _logManager = LimboLogs.Instance;
        }
        
        [Benchmark]
        public void Improved()
        {
            throw new NotImplementedException();
        }
        
        [Benchmark]
        public void Light()
        {
            NodeStatsLight stats = new NodeStatsLight(_node, _statsConfig);
        }
        
        [Benchmark]
        public void ImprovedRep()
        {
            throw new NotImplementedException();
        }
        
        [Benchmark]
        public long LightRep()
        {
            NodeStatsLight stats = new NodeStatsLight(_node, _statsConfig);
            return stats.CurrentNodeReputation;
        }
    }
}