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
using Nethermind.Crypto;
using Nethermind.HashLib;

namespace Nethermind.Benchmarks.Core
{
    public class Keccak512Benchmarks
    {
        private static HashLib.Crypto.SHA3.Keccak512 _hash = HashFactory.Crypto.SHA3.CreateKeccak512();
        
        private byte[] _a;

        private byte[][] _scenarios =
        {
            new byte[]{},
            new byte[]{1},
            new byte[100000],
            TestItem.AddressA.Bytes
        };

        [Params(0, 1, 2, 3)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public byte[] Improved()
        {
            throw new NotImplementedException();
        }
        
        [Benchmark]
        public byte[] Current()
        {
            return Keccak512.Compute(_a).Bytes;
        }
        
        [Benchmark]
        public byte[] HashLib()
        {
            return _hash.ComputeBytes(_a).GetBytes();
        }
    }
}
