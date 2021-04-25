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

using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpEncodeAccountBenchmark
    {
        private static Account _account;

        private Account[] _scenarios =
        {
            Account.TotallyEmpty,
            Build.An.Account.WithBalance(UInt256.Parse("0x1000000000000000000000", NumberStyles.HexNumber)).WithNonce(123).TestObject,
        };

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _account = _scenarios[ScenarioIndex];
        }
        
        [Benchmark]
        public byte[] Improved()
        {
            return Serialization.Rlp.Rlp.Encode(_account).Bytes;
        }
        
        [Benchmark]
        public byte[] Current()
        {
            return Serialization.Rlp.Rlp.Encode(_account).Bytes;
        }
    }
}
