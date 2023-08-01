// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core;
public class KeccakFBenchmarks
{
    private static ulong[] _a = new ulong[25];

    [Benchmark]
    public void Avx2()
    {
        KeccakHash.KeccakF1600_AVX2(_a);
    }

    [Benchmark(Baseline = true)]
    public void NonIntrinsic()
    {
        KeccakHash.KeccakF1600_64bit(_a);
    }
}
