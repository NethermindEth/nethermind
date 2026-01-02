// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpEncodeLongBenchmark
    {
        private long[] _scenarios;

        public RlpEncodeLongBenchmark()
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

        private long _value;

        [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _value = _scenarios[ScenarioIndex];

            Console.WriteLine($"Length current: {Current().Length}");
            Console.WriteLine($"Length improved: {Improved().Length}");
            Check(Current().Bytes, Improved().Bytes);
        }

        private void Check(byte[] a, byte[] b)
        {
            if (!a.SequenceEqual(b))
            {
                Console.WriteLine($"Outputs are different {a.ToHexString()} != {b.ToHexString()}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a.ToHexString()}");
        }

        [Benchmark]
        public Serialization.Rlp.Rlp Improved()
        {
            return Serialization.Rlp.Rlp.Encode(_value);
        }

        [Benchmark]
        public Serialization.Rlp.Rlp Current()
        {
            return Serialization.Rlp.Rlp.Encode(_value);
        }
    }
}
