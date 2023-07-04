// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        public byte[] Old()
        {
            return Bytes.Empty;
        }

        [Benchmark]
        public byte[] New()
        {
            return Bytes.Empty;
        }
    }
}
