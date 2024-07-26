// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Core
{
    public class BytesPadBenchmarks
    {
        private byte[] _a;

        private byte[][] _scenarios = new byte[][]
        {
            new byte[]{0},
            new byte[]{1},
            Keccak.Zero.BytesToArray(),
            TestItem.AddressA.Bytes
        };

        [Params(0, 1, 2, 3)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public (byte[], byte[]) Improved()
        {
            return (_a.PadLeft(32), _a.PadRight(32));
        }

        [Benchmark]
        public (byte[], byte[]) Current()
        {
            return (_a.PadLeft(32), _a.PadRight(32));
        }
    }
}
