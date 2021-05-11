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
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.HashLib;

namespace Nethermind.Benchmarks.Core
{
    public class Keccak256Benchmarks
    {
        private static HashLib.Crypto.SHA3.Keccak256 _hash = HashFactory.Crypto.SHA3.CreateKeccak256();
        
        private byte[] _a;

        private byte[][] _scenarios =
        {
            new byte[]{},
            new byte[]{1},
            new byte[100000],
            TestItem.AddressA.Bytes
        };

        [Params(1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex];
        }
        
        [Benchmark]
        public void MeadowHashSpan()
        {
            MeadowHashBenchmarks.ComputeHash(_a);
        }
        
        [Benchmark]
        public byte[] MeadowHashBytes()
        {
            return MeadowHashBenchmarks.ComputeHashBytes(_a);
        }
        
        [Benchmark(Baseline = true)]
        public byte[] Current()
        {
            return Keccak.Compute(_a).Bytes;
        }
        
        [Benchmark]
        public Span<byte> ValueKeccak()
        {
            return Nethermind.Core.Crypto.ValueKeccak.Compute(_a).BytesAsSpan;
        }
        
        
        [Benchmark]
        public byte[] HashLib()
        {
            return _hash.ComputeBytes(_a).GetBytes();
        }
    }
}
