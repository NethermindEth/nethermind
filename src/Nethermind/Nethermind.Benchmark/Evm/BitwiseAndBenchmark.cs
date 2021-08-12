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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Nethermind.Benchmarks.Evm
{
    public class BitwiseAndBenchmark
    {
        [GlobalSetup]
        public void Setup()
        {
            a[31] = 3;
            b[31] = 7;
        }

        private byte[] a = new byte[32];
        private byte[] b = new byte[32];
        private byte[] c = new byte[32];
        
        [Benchmark(Baseline = true)]
        public void Current()
        {
            ref var refA = ref MemoryMarshal.AsRef<ulong>(a);
            ref var refB = ref MemoryMarshal.AsRef<ulong>(b);
            ref var refBuffer = ref MemoryMarshal.AsRef<ulong>(c);

            refBuffer = refA & refB;
            Unsafe.Add(ref refBuffer, 1) = Unsafe.Add(ref refA, 1) & Unsafe.Add(ref refB, 1);
            Unsafe.Add(ref refBuffer, 2) = Unsafe.Add(ref refA, 2) & Unsafe.Add(ref refB, 2);
            Unsafe.Add(ref refBuffer, 3) = Unsafe.Add(ref refA, 3) & Unsafe.Add(ref refB, 3);
        }
        
        [Benchmark]
        public void Improved()
        {
            Vector<byte> aVec = new Vector<byte>(a);
            Vector<byte> bVec = new Vector<byte>(b);

            Vector.BitwiseAnd(aVec, bVec).CopyTo(c);
        }
    }
}
