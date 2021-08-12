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
using System.Collections;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Evm
{
    public class SignExtendBenchmark
    {
        [GlobalSetup]
        public void Setup()
        {
        }

        private byte[] a = new byte[32]
        {
            1, 17, 34, 50, 64, 78, 12, 56, 19, 12,
            120, 21, 123, 12, 76, 121, 1, 12, 23, 8,
            120, 21, 123, 12, 76, 121, 1, 12, 23, 8,
            120, 21
        };
        private byte[] b = new byte[32]
        {
            120, 21, 123, 12, 76, 121, 1, 12, 23, 8,
            77, 17, 34, 50, 64, 78, 12, 2, 19, 12,
            120, 21, 123, 12, 76, 121, 1, 12, 23, 8,
            55, 255
        };
        
        private byte[] c = new byte[32];
        
        [Benchmark(Baseline = true)]
        public void Current()
        {
            int position = 12;
            Span<byte> localB = this.b.AsSpan();
            BitArray bits1 = localB.ToBigEndianBitArray256();
            int bitPosition = Math.Max(0, 248 - 8 * (int)position);
            bool isSet = bits1[bitPosition];
            for (int i = 0; i < bitPosition; i++)
            {
                bits1[i] = isSet;
            }

            bits1.ToBytes().CopyTo(c.AsSpan());
        }

        private readonly byte[] BytesZero32 =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        };
        
        private readonly byte[] BytesMax32 =
        {
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255
        };
        
        [Benchmark]
        public void Improved()
        {
            int position = 12;
            Span<byte> localB = b.AsSpan();
            sbyte sign = (sbyte)localB[position];

            if (sign < 0)
            {
                BytesZero32.AsSpan().Slice(0, position).CopyTo(localB.Slice(0, position));
            }
            else
            {
                BytesMax32.AsSpan().Slice(0, position).CopyTo(localB.Slice(0, position));
            }
            
            localB.CopyTo(c);
        }
        
        [Benchmark]
        public void Improved2()
        {
            int position = 12;
            Span<byte> localB = b.AsSpan();
            sbyte sign = (sbyte)localB[position];

            Span<byte> signBytes = sign < 0 ? BytesZero32.AsSpan() : BytesMax32.AsSpan();
            signBytes.Slice(0, position).CopyTo(b.Slice(0, position));
            
            localB.CopyTo(c);
        }
    }
}
