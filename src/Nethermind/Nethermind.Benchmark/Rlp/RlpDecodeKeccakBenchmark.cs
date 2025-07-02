// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeKeccakBenchmark
    {
        private RlpStream[] _scenariosContext;
        private byte[][] _scenarios;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _scenarios = new[]
            {
                Serialization.Rlp.Rlp.Encode(Keccak.Zero).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.EmptyTreeHash).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptyString).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptySequenceRlp).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptyString).Bytes.Concat(new byte[100000]).ToArray(),
                Serialization.Rlp.Rlp.Encode(Keccak.Compute("a")).Bytes.Concat(new byte[100000]).ToArray()
            };
        }

        [IterationSetup]
        public void Setup()
        {
            _scenariosContext = _scenarios.Select(s => new RlpStream(s)).ToArray();
        }

        [Params(0, 1, 2, 3)]
        public int ScenarioIndex { get; set; }

        [Benchmark]
        public Hash256 Current()
        {
            return _scenariosContext[ScenarioIndex].DecodeKeccak();
        }
    }
}
