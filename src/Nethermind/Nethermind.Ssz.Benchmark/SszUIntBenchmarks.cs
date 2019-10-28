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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Ssz.Benchmarks
{
    [CoreJob]
    public class SszUIntBenchmarks
    {
        [Benchmark(Baseline = true)]
        public void Current()
        {
            Span<byte> output = stackalloc byte[32];
            
            Ssz.EncodeInt8(output,0);
            Ssz.EncodeInt16(output,0);
            Ssz.EncodeInt32(output,0);
            Ssz.EncodeInt64(output,0);
            Ssz.EncodeInt128(output,UInt128.Zero);
            Ssz.EncodeInt256(output,UInt256.Zero);
            
            Ssz.EncodeInt8(output,1);
            Ssz.EncodeInt16(output,1);
            Ssz.EncodeInt32(output,1);
            Ssz.EncodeInt64(output,1UL);
            Ssz.EncodeInt128(output, UInt128.One);
            Ssz.EncodeInt256(output, UInt256.One);
            
            Ssz.EncodeInt8(output, byte.MaxValue);
            Ssz.EncodeInt16(output, ushort.MaxValue);
            Ssz.EncodeInt32(output, uint.MaxValue);
            Ssz.EncodeInt64(output, ulong.MaxValue);
            Ssz.EncodeInt128(output, UInt128.MaxValue);
            Ssz.EncodeInt256(output, UInt256.MaxValue);
        }
    }
}