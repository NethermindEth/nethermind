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

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Benchmarks.Evm
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class Bn128PairingBenchmark
    {
        private IPrecompiledContract _precompile = Bn128PairingPrecompiledContract.Instance;
        
        private byte[] _data;

        private (byte[] A, byte[] B)[] _scenarios = new[]
        {
            (Bytes.FromHexString("0x030644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd3"), Bytes.FromHexString("0x15ed738c0e0a7c92e7845f96b2ae9c0a68a6a449e3538fc7ff3ebf7a5a18a2c4")),            
            // valid scenarios to be added
        };

        [Params(0)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _ = Bn128AddPrecompiledContract.Instance;
            Span<byte> bytes = new byte[64];
            _scenarios[ScenarioIndex].A.AsSpan().CopyTo(bytes.Slice(0, 32));
            _scenarios[ScenarioIndex].B.AsSpan().CopyTo(bytes.Slice(32, 32));
            _data = bytes.ToArray();
        }
        
        [Benchmark]
        public (byte[], bool) Improved()
        {
            return _precompile.Run(_data);
        }
        
        [Benchmark]
        public (byte[], bool) Current()
        {
            return _precompile.Run(_data);
        }
    }
}