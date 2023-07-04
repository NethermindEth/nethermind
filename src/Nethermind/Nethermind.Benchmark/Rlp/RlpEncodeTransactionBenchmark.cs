// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpEncodeTransactionBenchmark
    {
        private Transaction[] _scenarios;

        public RlpEncodeTransactionBenchmark()
        {
            _scenarios = new[]
            {
                Build.A.Transaction.TestObject,
            };
        }

        [Params(0)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            Console.WriteLine($"Length current: {Current().Length}");
            Console.WriteLine($"Length improved: {Improved().Length}");
            Check(Current(), Improved());
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
        public byte[] Improved()
        {
            throw new NotImplementedException();
        }

        [Benchmark]
        public byte[] Current()
        {
            return Serialization.Rlp.Rlp.Encode(_scenarios[ScenarioIndex]).Bytes;
        }
    }
}
