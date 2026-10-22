// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Benchmarks
{
    public class DiscoveryBenchmarks
    {
        [GlobalSetup]
        public void GlobalSetup()
        {
        }

        [Benchmark(Baseline = true)]
        public byte[] Old() => Bytes.Empty;

        [Benchmark]
        public byte[] New() => Bytes.Empty;
    }
}
