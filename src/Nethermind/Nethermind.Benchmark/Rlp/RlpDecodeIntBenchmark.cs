// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeIntBenchmark
    {
        private int[] _scenarios;

        public RlpDecodeIntBenchmark()
        {
            _scenarios = new[]
            {
                int.MinValue,
                -1,
                0,
                1,
                128,
                256,
                256 * 256,
                int.MaxValue
            };
        }

        private byte[] _value;

        [Params(0, 1, 2, 3, 4, 5, 6, 7)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _value = Serialization.Rlp.Rlp.Encode(_scenarios[ScenarioIndex]).Bytes;

            Check(Current(), Improved());
        }

        private void Check(int a, int b)
        {
            if (a != b)
            {
                Console.WriteLine($"Outputs are different {a} != {b}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a}");
        }

        [Benchmark]
        public int Improved()
        {
            return new RlpStream(_value).DecodeInt();
        }

        [Benchmark]
        public int Current()
        {
            return new RlpStream(_value).DecodeInt();
        }
    }
}
