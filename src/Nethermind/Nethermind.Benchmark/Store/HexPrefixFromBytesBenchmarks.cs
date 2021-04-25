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

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Blooms;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.Store
{
    public class HexPrefixFromBytesBenchmarks
    {
        private byte[] _a;

        private byte[][] _scenarios = new byte[][]
        {
            Keccak.EmptyTreeHash.Bytes,
            Keccak.Zero.Bytes,
            TestItem.AddressA.Bytes,
        };

        [Params(0, 1, 2)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public byte Improved()
        {
            return HexPrefix.FromBytes(_a).Path[0];
        }

        [Benchmark(Baseline = true)]
        public byte Current()
        {
            return HexPrefix.FromBytes(_a).Path[0];
        }
    }
}
