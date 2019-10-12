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

using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Benchmarks.Evm
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class BitwiseNotBenchmark
    {
        [GlobalSetup]
        public void Setup()
        {
            a[31] = 3;
        }

        private byte[] a = new byte[32];
        private byte[] c = new byte[32];
        
        internal readonly byte[] BytesMax32 =
        {
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255
        };
        
        [Benchmark(Baseline = true)]
        public void Current()
        {
            for (int i = 0; i < 32; i++)
            {
                c[i] = (byte)(~a[i]);
            }
        }
        
        [Benchmark]
        public void Improved()
        {
            Vector<byte> aVec = new Vector<byte>(a);
            Vector.Xor(aVec, new Vector<byte>(BytesMax32)).CopyTo(c);
        }
    }
}