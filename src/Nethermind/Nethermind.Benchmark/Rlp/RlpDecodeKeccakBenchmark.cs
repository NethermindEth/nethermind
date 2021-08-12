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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeKeccakBenchmark
    {
        private RlpStream[] _scenariosContext;
        private byte[][] _scenarios;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _scenarios = new[]
            {
                Serialization.Rlp.Rlp.Encode(Keccak.Zero).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.EmptyTreeHash).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptyString).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptySequenceRlp).Bytes,
                Serialization.Rlp.Rlp.Encode(Keccak.OfAnEmptyString).Bytes.Concat(new byte[100000]).ToArray(),
                Serialization.Rlp.Rlp.Encode(Keccak.Compute("a")).Bytes.Concat(new byte[100000]).ToArray()
            };
        }
        
        [IterationSetup]
        public void Setup()
        {
            _scenariosContext = _scenarios.Select(s => new RlpStream(s)).ToArray();
        }

        [Params(0, 1, 2, 3)]
        public int ScenarioIndex { get; set; }

        [Benchmark]
        public Keccak Current()
        {
            return _scenariosContext[ScenarioIndex].DecodeKeccak();
        }
    }
}
