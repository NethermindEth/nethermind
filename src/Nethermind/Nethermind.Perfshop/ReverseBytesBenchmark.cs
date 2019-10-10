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
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;

namespace Nethermind.Perfshop
{
    [DisassemblyDiagnoser]
    public class ReverseBytesBenchmark
    {
        private byte[] Bytes = Core.Extensions.Bytes.FromHexString("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        private byte[] BytesReversed = Core.Extensions.Bytes.FromHexString("0x1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");
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

            byte[] clone = Bytes.Clone() as byte[];

            ArrayVersion();
            if (!Core.Extensions.Bytes.AreEqual(Bytes, BytesReversed))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(ArrayVersion)}");
            }
            
            ArrayVersion();
            if (!Core.Extensions.Bytes.AreEqual(clone, Bytes))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(ArrayVersion)}");
            }

            Avx2Version();
            if (!Core.Extensions.Bytes.AreEqual(Bytes, BytesReversed))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(Avx2Version)}");
            }
            
            Avx2Version();
            if (!Core.Extensions.Bytes.AreEqual(clone, Bytes))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(Avx2Version)}");
            }

            SpanVersion();
            if (!Core.Extensions.Bytes.AreEqual(Bytes, BytesReversed))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(SpanVersion)}");
            }
            
            SpanVersion();
            if (!Core.Extensions.Bytes.AreEqual(clone, Bytes))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(SpanVersion)}");
            }

            LoopVersion();
            if (!Core.Extensions.Bytes.AreEqual(Bytes, BytesReversed))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(LoopVersion)}");
            }
            
            LoopVersion();
            if (!Core.Extensions.Bytes.AreEqual(clone, Bytes))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(LoopVersion)}");
            }

            SwapVersion();
            if (!Core.Extensions.Bytes.AreEqual(Bytes, BytesReversed))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(SwapVersion)}");
            }
            
            SwapVersion();
            if (!Core.Extensions.Bytes.AreEqual(clone, Bytes))
            {
                throw new InvalidBenchmarkDeclarationException($"{nameof(SwapVersion)}");
            }
        }

        [Benchmark]
        public void ArrayVersion()
        {
            Array.Reverse(Bytes);
        }

        private static byte[] _reverseMask = {15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0};

        [Benchmark]
        public void Avx2Version()
        {
            byte[] bytes = Bytes;
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

        [Benchmark]
        public void SwapVersion()
        {
            Span<ulong> ulongs = MemoryMarshal.Cast<byte, ulong>(Bytes);
            (ulongs[0], ulongs[3]) = (BinaryPrimitives.ReverseEndianness(ulongs[3]), BinaryPrimitives.ReverseEndianness(ulongs[0]));
            (ulongs[1], ulongs[2]) = (BinaryPrimitives.ReverseEndianness(ulongs[2]), BinaryPrimitives.ReverseEndianness(ulongs[1]));
        }

        [Benchmark]
        public void SpanVersion()
        {
            Bytes.AsSpan().Reverse();
        }

        [Benchmark(Baseline = true)]
        public void LoopVersion()
        {
            byte[] bytes = Bytes;
            for (int i = 0; i < bytes.Length / 2; i++)
            {
                (bytes[i], bytes[bytes.Length - i - 1]) = (bytes[bytes.Length - i - 1], bytes[i]);
            }
        }
    }
}