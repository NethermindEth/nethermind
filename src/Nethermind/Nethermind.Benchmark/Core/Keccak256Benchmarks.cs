// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
//using Nethermind.HashLib;

namespace Nethermind.Benchmarks.Core
{
    public class Keccak256Benchmarks
    {
        //private static HashLib.Crypto.SHA3.Keccak256 _hash = HashFactory.Crypto.SHA3.CreateKeccak256();

        private byte[] _a;

        private byte[][] _scenarios =
        {
            new byte[]{},
            new byte[]{1},
            new byte[100000],
            TestItem.AddressA.Bytes
        };

        [Params(1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public void MeadowHashSpan()
        {
            MeadowHashBenchmarks.ComputeHash(_a);
        }

        [Benchmark]
        public byte[] MeadowHashBytes()
        {
            return MeadowHashBenchmarks.ComputeHashBytes(_a);
        }

        [Benchmark(Baseline = true)]
        public byte[] Current()
        {
            return Keccak.Compute(_a).BytesToArray();
        }

        [Benchmark]
        public Span<byte> ValueKeccak()
        {
            return Nethermind.Core.Crypto.ValueKeccak.Compute(_a).BytesAsSpan;
        }

        //[Benchmark]
        //public byte[] HashLib()
        //{
        //    return _hash.ComputeBytes(_a).GetBytes();
        //}
    }
}
