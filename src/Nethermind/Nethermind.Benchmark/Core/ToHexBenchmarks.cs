// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Core
{
    public class ToHexBenchmarks
    {
        private byte[] bytes = TestItem.KeccakA.BytesToArray();

        [Params(true, false)]
        public bool OddNumber;

        [GlobalSetup]
        public void Setup()
        {
            //Test Performance of odd number
            if (OddNumber)
                bytes = bytes.Slice(1).ToArray();
        }

        [Benchmark(Baseline = true)]
        public string Current()
        {
            return bytes.ToHexString();
        }

        [Benchmark]
        public string Improved()
        {
            return HexConverter.ToString(bytes);
        }
    }
}
