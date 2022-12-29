// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Core
{
    public class FromHexBenchmarks
    {
        private string array = Bytes.FromHexString("0123456789abcdef").ToHexString();

        [GlobalSetup]
        public void Setup()
        {

        }

        [Benchmark(Baseline = true)]
        public byte[] Current()
        {
            return Bytes.FromHexString(array);
        }

        [Benchmark]
        public byte[] Improved()
        {
#pragma warning disable CS0612 // Type or member is obsolete
            return Bytes.FromHexStringOld(array);
#pragma warning restore CS0612 // Type or member is obsolete
        }
    }
}
