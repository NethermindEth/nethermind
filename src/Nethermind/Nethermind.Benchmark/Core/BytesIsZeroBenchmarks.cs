// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Core
{
    public class BytesIsZeroBenchmarks
    {
        private byte[] _a;

        private byte[][] _scenarios = new byte[][]
        {
            Keccak.Zero.BytesToArray(),
            Keccak.EmptyTreeHash.BytesToArray(),
            Keccak.OfAnEmptyString.BytesToArray(),
            TestItem.AddressA.Bytes,
            Address.Zero.Bytes,
        };

        [Params(0, 1, 2, 3, 4)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public bool Improved()
        {
            return _a.IsZero();
        }

        [Benchmark]
        public bool Current()
        {
            return _a.IsZero();
        }
    }
}
