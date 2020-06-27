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
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Dirichlet.Benchmark
{
    [MemoryDiagnoser]
    [ShortRunJob]
    // [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class ConversionBenchmarks
    {
        private BigInteger[] _scenariosBI = new BigInteger[3];
        private UInt256[] _scenariosU = new UInt256[3];

        [Params(0, 1, 2)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            UInt256.CreateFromBigEndian(out UInt256 a, Bytes.FromHexString("0xA0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7").AsSpan());
            UInt256.CreateFromBigEndian(out UInt256 b, Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000").AsSpan());
            UInt256.CreateFromBigEndian(out UInt256 c, Bytes.FromHexString("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF").AsSpan());

            _scenariosU[0] = a;
            _scenariosU[1] = b;
            _scenariosU[2] = c;

            _scenariosBI[0] = a;
            _scenariosBI[1] = b;
            _scenariosBI[2] = c;
        }

        [Benchmark]
        public BigInteger Current_uint256_to_bigint()
        {
            return _scenariosU[ScenarioIndex];
        }
        
        [Benchmark]
        public UInt256 Current_bigint_to_uint256()
        {
            UInt256.CreateOld(out UInt256 res, _scenariosBI[ScenarioIndex]);
            return res;
        }
        
        [Benchmark]
        public UInt256 Improved_bigint_to_uint256()
        {
            UInt256.Create(out UInt256 res, _scenariosBI[ScenarioIndex]);
            return res;
        }
    }
}