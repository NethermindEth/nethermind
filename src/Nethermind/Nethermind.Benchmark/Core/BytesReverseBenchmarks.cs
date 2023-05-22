// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
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
            Keccak.Zero.BytesToArray(),
            Keccak.EmptyTreeHash.BytesToArray(),
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
            if (_a.Length == 32)
            {
                Swap(_a);
            }
            else
            {
                Span<byte> bytesPadded = stackalloc byte[32];
                _a.CopyTo(bytesPadded);

                Swap(bytesPadded);

                bytesPadded.Slice(32 - _a.Length);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Swap(Span<byte> bytes)
        {
            Span<ulong> ulongs = MemoryMarshal.Cast<byte, ulong>(bytes);
            (ulongs[0], ulongs[3]) = (BinaryPrimitives.ReverseEndianness(ulongs[3]), BinaryPrimitives.ReverseEndianness(ulongs[0]));
            (ulongs[1], ulongs[2]) = (BinaryPrimitives.ReverseEndianness(ulongs[2]), BinaryPrimitives.ReverseEndianness(ulongs[1]));
        }

        private static byte[] _reverseMask = { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };

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
