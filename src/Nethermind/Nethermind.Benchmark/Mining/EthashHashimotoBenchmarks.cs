/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Mining;

namespace Nethermind.Benchmarks.Mining
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class EthashHashimotoBenchmarks
    {
        private Ethash _ethash = new Ethash(NullLogManager.Instance);
        
        private BlockHeader _header;

        private BlockHeader[] _scenarios =
        {
            Build.A.BlockHeader.WithNumber(1).WithDifficulty(100).TestObject,
            Build.A.BlockHeader.WithNumber(1).WithDifficulty(100).TestObject,
        };

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _header = _scenarios[ScenarioIndex];
        }
        
        [Benchmark]
        public (Keccak, ulong) Improved()
        {
            return _ethash.Mine(_header, 0UL);
        }
        
        [Benchmark]
        public (Keccak, ulong) Current()
        {
            return _ethash.Mine(_header, 0UL);
        }
    }
}