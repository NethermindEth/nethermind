// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Core
{
    public class Keccak256Benchmarks
    {
        private byte[] _a;
        byte[] _output = new byte[32];

        private static readonly byte[][] _scenarios =
        {
            new byte[]{},
            TestItem.AddressA.Bytes,
            UInt256.One.ToBigEndian(),
            new byte[100000],
        };

        [Params(0,1,2,3)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public void Avx512()
        {
            KeccakHash.BenchmarkHash(_a, _output, useAvx512: true);
        }

        [Benchmark(Baseline = true)]
        public void Scalar()
        {
            KeccakHash.BenchmarkHash(_a, _output, useAvx512: false);
        }
    }
}
