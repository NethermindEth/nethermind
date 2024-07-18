// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeLongBenchmark
    {
        private long[] _scenarios;

        public RlpDecodeLongBenchmark()
        {
            _scenarios = new[]
            {
                long.MinValue,
                -1,
                0,
                1,
                128,
                256,
                256 * 256,
                256 * 256 * 256,
                256 * 256 * 256 * 256L,
                256 * 256 * 256 * 256L * 256L,
                256 * 256 * 256 * 256L * 256 * 256,
                256 * 256 * 256 * 256L * 256 * 256 * 256,
                long.MaxValue
            };
        }

        private byte[] _value;

        [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _value = Serialization.Rlp.Rlp.Encode(_scenarios[ScenarioIndex]).Bytes;

            Check(Current(), Improved());
        }

        private void Check(long a, long b)
        {
            if (a != b)
            {
                Console.WriteLine($"Outputs are different {a} != {b}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a}");
        }

        [Benchmark]
        public long Improved()
        {
            return new RlpStream(_value).DecodeLong();
        }

        [Benchmark]
        public long Current()
        {
            return new RlpStream(_value).DecodeLong();
        }
    }
}
