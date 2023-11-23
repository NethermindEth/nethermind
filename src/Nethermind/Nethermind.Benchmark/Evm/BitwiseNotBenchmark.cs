// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Benchmarks.Evm
{
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
            ref var refA = ref MemoryMarshal.AsRef<ulong>(a);
            ref var refBuffer = ref MemoryMarshal.AsRef<ulong>(c);

            refBuffer = ~refA;
            Unsafe.Add(ref refBuffer, 1) = ~Unsafe.Add(ref refA, 1);
            Unsafe.Add(ref refBuffer, 2) = ~Unsafe.Add(ref refA, 2);
            Unsafe.Add(ref refBuffer, 3) = ~Unsafe.Add(ref refA, 3);
        }

        [Benchmark]
        public void Improved()
        {
            Vector<byte> aVec = new Vector<byte>(a);
            Vector.Xor(aVec, new Vector<byte>(BytesMax32)).CopyTo(c);
        }
    }
}
