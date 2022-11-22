// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Nethermind.Ssz.Benchmarks
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class SszBoolBenchmarks
    {
        [Benchmark(Baseline = true)]
        public byte Current()
        {
            return Ssz.Encode(true) > Ssz.Encode(false) ? (byte)1 : (byte)0;
        }
    }
}
