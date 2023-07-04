// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Evm
{
    public class MemoryCostBenchmark
    {
        private IEvmMemory _current = new EvmPooledMemory();
        private IEvmMemory _improved = new EvmPooledMemory();

        private UInt256 _location;
        private UInt256 _length;

        private (UInt256 Location, UInt256 Length)[] _scenarios = new[]
        {
            (UInt256.Zero, (UInt256)32),
            (UInt256.Zero, UInt256.MaxValue),
            ((UInt256)1000, (UInt256)72)
        };

        [Params(0, 1, 2)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _location = _scenarios[ScenarioIndex].Location;
            _length = _scenarios[ScenarioIndex].Length;
        }

        [Benchmark]
        public long Current()
        {
            UInt256 dest = _location;
            return _current.CalculateMemoryCost(in dest, _length);
        }
    }
}
