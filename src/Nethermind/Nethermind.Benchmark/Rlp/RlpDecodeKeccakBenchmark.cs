// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeKeccakBenchmark
    {
        private byte[][] _scenarios = null!;

        [GlobalSetup]
        public void GlobalSetup() => _scenarios = new[]
            {
                Serialization.Rlp.Rlp.Encode(Keccak.Zero).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.EmptyTreeHash).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptyString).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptySequenceRlp).Bytes,
            };

        [Params(0, 1, 2, 3)]
        public int ScenarioIndex { get; set; }

        [Benchmark]
        public Hash256 Current()
        {
            Serialization.Rlp.RlpReader ctx = new(_scenarios[ScenarioIndex]);
            return ctx.DecodeKeccakNonNull();
        }
    }
}
