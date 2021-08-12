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

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpEncodeTransactionBenchmark
    {
        private Transaction[] _scenarios;

        public RlpEncodeTransactionBenchmark()
        {
            _scenarios = new[]
            {
                Build.A.Transaction.TestObject,
            };
        }

        [Params(0)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            Console.WriteLine($"Length current: {Current().Length}");
            Console.WriteLine($"Length improved: {Improved().Length}");
            Check(Current(), Improved());
        }

        private void Check(byte[] a, byte[] b)
        {
            if (!a.SequenceEqual(b))
            {
                Console.WriteLine($"Outputs are different {a.ToHexString()} != {b.ToHexString()}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a.ToHexString()}");
        }

        [Benchmark]
        public byte[] Improved()
        {
            throw new NotImplementedException();
        }

        [Benchmark]
        public byte[] Current()
        {
            return Serialization.Rlp.Rlp.Encode(_scenarios[ScenarioIndex]).Bytes;
        }
    }
}
