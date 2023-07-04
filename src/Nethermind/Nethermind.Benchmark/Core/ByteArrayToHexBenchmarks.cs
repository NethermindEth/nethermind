// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using HexMate;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Core
{
    public class ByteArrayToHexBenchmarks
    {
        private byte[] array = Bytes.FromHexString("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        [GlobalSetup]
        public void Setup()
        {

        }

        [Benchmark]
        public string Improved()
        {
            return Bytes.ByteArrayToHexViaLookup32Safe(array, false);
        }

        [Benchmark]
        public string SafeLookup()
        {
            return Bytes.ByteArrayToHexViaLookup32Safe(array, false);
        }

        [Benchmark(Baseline = true)]
        public string HexMateA()
        {
            return Convert.ToHexString(array, HexFormattingOptions.Lowercase);
        }
    }
}
