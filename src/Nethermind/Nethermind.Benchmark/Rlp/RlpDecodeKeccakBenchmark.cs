// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeKeccakBenchmark
    {
        private byte[][] _scenarios;

        [GlobalSetup]
        public void GlobalSetup() => _scenarios = new[]
            {
                Serialization.Rlp.Rlp.Encode(Keccak.Zero),
                Serialization.Rlp.Rlp.Encode(Keccak.EmptyTreeHash),
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptyString),
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptySequenceRlp),
            };

        [Params(0, 1, 2, 3)]
        public int ScenarioIndex { get; set; }

        [Benchmark]
        public Hash256 Current()
        {
            RlpReader ctx = new(_scenarios[ScenarioIndex]);
            return ctx.DecodeKeccak();
        }
    }
}
