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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Ssz.Benchmarks
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class SszUIntBenchmarks
    {
        [Benchmark(Baseline = true)]
        public void Current()
        {
            Span<byte> output = stackalloc byte[32];
            
            Ssz.Encode(output,0);
            Ssz.Encode(output,0);
            Ssz.Encode(output,0);
            Ssz.Encode(output,0);
            Ssz.Encode(output,UInt128.Zero);
            Ssz.Encode(output,UInt256.Zero);
            
            Ssz.Encode(output,1);
            Ssz.Encode(output,1);
            Ssz.Encode(output,1);
            Ssz.Encode(output,1UL);
            Ssz.Encode(output, UInt128.One);
            Ssz.Encode(output, UInt256.One);
            
            Ssz.Encode(output, byte.MaxValue);
            Ssz.Encode(output, ushort.MaxValue);
            Ssz.Encode(output, uint.MaxValue);
            Ssz.Encode(output, ulong.MaxValue);
            Ssz.Encode(output, UInt128.MaxValue);
            Ssz.Encode(output, UInt256.MaxValue);
        }
    }
}