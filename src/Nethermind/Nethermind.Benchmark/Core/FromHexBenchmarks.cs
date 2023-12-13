// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Core
{
    public class FromHexBenchmarks
    {
        private string hex = "0123456789abcdef";

        [Params(true, false)]
        public bool With0xPrefix;

        [Params(true, false)]
        public bool OddNumber;

        [GlobalSetup]
        public void Setup()
        {
            //Test Performance of odd number
            if (OddNumber)
                hex = "5" + hex;

            //Test performance of hex
            if (With0xPrefix)
                hex = "0x" + hex;
        }

        [Benchmark(Baseline = true)]
        public byte[] Current()
        {
            return Bytes.FromHexString(hex);
        }

        [Benchmark]
        public byte[] Improved()
        {
            return Bytes.FromHexString(hex);
        }
    }
}
