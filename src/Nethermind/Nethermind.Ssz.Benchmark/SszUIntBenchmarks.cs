// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Dirichlet.Numerics;
using UInt128 = Nethermind.Dirichlet.Numerics.UInt128;

namespace Nethermind.Ssz.Benchmarks
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class SszUIntBenchmarks
    {
        [Benchmark(Baseline = true)]
        public void Current()
        {
            Span<byte> output = stackalloc byte[32];

            Ssz.Encode(output, 0);
            Ssz.Encode(output, 0);
            Ssz.Encode(output, 0);
            Ssz.Encode(output, 0);
            Ssz.Encode(output, UInt128.Zero);
            Ssz.Encode(output, UInt256.Zero);

            Ssz.Encode(output, 1);
            Ssz.Encode(output, 1);
            Ssz.Encode(output, 1);
            Ssz.Encode(output, 1UL);
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
