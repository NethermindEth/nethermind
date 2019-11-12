﻿/*
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

using System.Globalization;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Benchmarks.Rlp
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class RlpDecodeAccountBenchmark
    {
        private static byte[] _account;

        private byte[][] _scenarios =
        {
            Nethermind.Core.Encoding.Rlp.Encode(Account.TotallyEmpty).Bytes,
            Nethermind.Core.Encoding.Rlp.Encode(Build.An.Account.WithBalance(UInt256.Parse("0x1000000000000000000000", NumberStyles.HexNumber)).WithNonce(123).TestObject).Bytes,
        };

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _account = _scenarios[ScenarioIndex];
        }
        
        [Benchmark]
        public Account Improved()
        {
            return Nethermind.Core.Encoding.Rlp.Decode<Account>(_account);
        }
        
        [Benchmark]
        public Account Current()
        {
            return Nethermind.Core.Encoding.Rlp.Decode<Account>(_account);
        }
    }
}