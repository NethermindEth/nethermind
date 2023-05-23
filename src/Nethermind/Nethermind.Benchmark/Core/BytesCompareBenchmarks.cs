// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Core
{
    public class BytesCompareBenchmarks
    {
        private byte[] _a;
        private byte[] _b;

        private (byte[] A, byte[] B)[] _scenarios = new[]
        {
            (Keccak.Zero.BytesToArray(), Keccak.Zero.BytesToArray()),
            (Keccak.Zero.BytesToArray(), Keccak.EmptyTreeHash.BytesToArray()),
            (Keccak.EmptyTreeHash.BytesToArray(), Keccak.EmptyTreeHash.BytesToArray()),
            (Keccak.OfAnEmptyString.BytesToArray(), Keccak.EmptyTreeHash.BytesToArray()),
            (Keccak.OfAnEmptyString.BytesToArray(), Keccak.EmptyTreeHash.BytesToArray()),
            (TestItem.AddressA.Bytes, TestItem.AddressB.Bytes),
        };

        [Params(0, 1, 2, 3, 4, 5)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex].A;
            _b = _scenarios[ScenarioIndex].B;
        }

        [Benchmark]
        public bool Improved()
        {
            return Bytes.AreEqual(_a, _b);
        }

        [Benchmark]
        public bool Current()
        {
            return Bytes.AreEqual(_a, _b);
        }
    }
}
