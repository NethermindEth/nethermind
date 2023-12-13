// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Benchmarks.Evm
{
    public class BitwiseXorBenchmark
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

            refBuffer = refA ^ refB;
            Unsafe.Add(ref refBuffer, 1) = Unsafe.Add(ref refA, 1) ^ Unsafe.Add(ref refB, 1);
            Unsafe.Add(ref refBuffer, 2) = Unsafe.Add(ref refA, 2) ^ Unsafe.Add(ref refB, 2);
            Unsafe.Add(ref refBuffer, 3) = Unsafe.Add(ref refA, 3) ^ Unsafe.Add(ref refB, 3);
        }

        [Benchmark]
        public void Improved()
        {
            Vector<byte> aVec = new Vector<byte>(a);
            Vector<byte> bVec = new Vector<byte>(b);
            Vector.Xor(aVec, bVec).CopyTo(c);
        }
    }
}
