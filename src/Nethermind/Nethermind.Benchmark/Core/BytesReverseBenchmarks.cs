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
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Core
{
    public class BytesReverseBenchmarks
    {
        private byte[] _a;

        private byte[][] _scenarios = new[]
        {
            Keccak.Zero.Bytes,
            Keccak.EmptyTreeHash.Bytes,
            TestItem.AddressA.Bytes
        };

        [Params(0, 1, 2)]
        public int ScenarioIndex { get; set; }

        private static Vector256<byte> _shuffleMask;

        [GlobalSetup]
        public void Setup()
        {
            unsafe
            {
                fixed (byte* ptr_mask = _reverseMask)
                {
                    _shuffleMask = Avx2.LoadVector256(ptr_mask);
                }
            }
            
            _a = _scenarios[ScenarioIndex];
        }

        [Benchmark(Baseline = true)]
        public byte[] Current()
        {
            return Bytes.Reverse(_a);
        }

        [Benchmark]
        public void Improved()
        {
            _a.AsSpan().Reverse();
        }
        
        [Benchmark]
        public void SwapVersion()
        {
            Span<ulong> ulongs = MemoryMarshal.Cast<byte, ulong>(_a);
            (ulongs[0], ulongs[3]) = (BinaryPrimitives.ReverseEndianness(ulongs[3]), BinaryPrimitives.ReverseEndianness(ulongs[0]));
            (ulongs[1], ulongs[2]) = (BinaryPrimitives.ReverseEndianness(ulongs[2]), BinaryPrimitives.ReverseEndianness(ulongs[1]));
        }
        
        private static byte[] _reverseMask = {15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0};

        [Benchmark]
        public void Avx2Version()
        {
            byte[] bytes = _a;
            unsafe
            {
                fixed (byte* ptr_bytes = bytes)
                {
                    Vector256<byte> inputVector = Avx2.LoadVector256(ptr_bytes);
                    Vector256<byte> result = Avx2.Shuffle(inputVector, _shuffleMask);
                    result = Avx2.Permute4x64(result.As<byte, ulong>(), 0b01001110).As<ulong, byte>();
                    Avx2.Store(ptr_bytes, result);
                }
            }
        }
    }
}
