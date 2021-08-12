//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
