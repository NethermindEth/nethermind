// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpEncodeTransactionBenchmark
    {
        private Transaction[] _scenarios;

        public RlpEncodeTransactionBenchmark() => _scenarios = new[]
            {
                Build.A.Transaction.TestObject,
            };

        [Params(0)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup() => Console.WriteLine($"Length current: {Current().Length}");

        [Benchmark]
        public byte[] Current() => Serialization.Rlp.Rlp.Encode(_scenarios[ScenarioIndex]).Bytes;
    }
}
