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
            Keccak.Zero.Bytes,
            Keccak.EmptyTreeHash.Bytes,
            Keccak.OfAnEmptyString.Bytes,
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
