//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Baseline.Tree;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Baseline.Benchmark
{
    [MemoryDiagnoser]
    [SimpleJob(RuntimeMoniker.NetCoreApp31, targetCount:10)]
    public class BuildingTreeBenchmarks
    {
        private Keccak[] _testLeaves;

        [Params(32, 1000, 100000)]
        public int NumberOfLeaves { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _testLeaves = new Keccak[NumberOfLeaves];
            var _truncationLength = 0;
            for (int i = 0; i < _testLeaves.Length; i++)
            {
                byte[] bytes = new byte[32];
                bytes[i % (32 - _truncationLength) + _truncationLength] = (byte)(i + 1);
                _testLeaves[i] = new Keccak(bytes);
            }
        }

        [Benchmark]
        public void BuildTreeWithInstantHashing()
        {
            BaselineTree baselineTree = new ShaBaselineTree(new MemDb(), new MemDb(), Array.Empty<byte>(), 0, LimboNoErrorLogger.Instance);
            for (uint i = 0; i < _testLeaves.Length; ++i)
            {
                baselineTree.Insert(_testLeaves[i]);
            }
        }

        [Benchmark]
        public void BuildTreeWithHashingInTheEnd()
        {
            BaselineTree baselineTree = new ShaBaselineTree(new MemDb(), new MemDb(), Array.Empty<byte>(), 0, LimboNoErrorLogger.Instance);
            for (uint i = 0; i < _testLeaves.Length; ++i)
            {
                baselineTree.Insert(_testLeaves[i], false);
            }

            baselineTree.CalculateHashes();
        }

        [Benchmark]
        public void InsertingValuesWithoutCalculatingHashes()
        {
            BaselineTree baselineTree = new ShaBaselineTree(new MemDb(), new MemDb(), Array.Empty<byte>(), 0, LimboNoErrorLogger.Instance);
            for (uint i = 0; i < _testLeaves.Length; ++i)
            {
                baselineTree.Insert(_testLeaves[i], false);
            }
        }
    }

    [MemoryDiagnoser]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class BigTreeBuildingBenchmarks
    {
        private Keccak[] _testLeaves;

        [Params(1000000)]
        public int NumberOfLeaves { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _testLeaves = new Keccak[NumberOfLeaves];
            var _truncationLength = 0;
            for (int i = 0; i < _testLeaves.Length; i++)
            {
                byte[] bytes = new byte[32];
                bytes[i % (32 - _truncationLength) + _truncationLength] = (byte)(i + 1);
                _testLeaves[i] = new Keccak(bytes);
            }
        }

        [Benchmark]
        public void BuildTreeWithHashingInTheEnd()
        {
            BaselineTree baselineTree = new ShaBaselineTree(new MemDb(), new MemDb(), Array.Empty<byte>(), 0, LimboNoErrorLogger.Instance);
            for (uint i = 0; i < _testLeaves.Length; ++i)
            {
                baselineTree.Insert(_testLeaves[i], false);
            }

            baselineTree.CalculateHashes();
        }
    }
}
