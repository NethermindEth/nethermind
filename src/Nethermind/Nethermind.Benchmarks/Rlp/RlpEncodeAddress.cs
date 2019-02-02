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
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Rlp
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class RlpEncodeAddress
    {
        private static Address _address;

        [Params(true, false)] public bool AllZeros { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            byte[] a = new byte[20];
            if (!AllZeros)
            {
                a = new CryptoRandom().GenerateRandomBytes(20);
            }
            
            _address = new Address(a);
        }
        
        [Benchmark]
        public byte[] Improved()
        {
            throw new NotImplementedException();
        }
        
        [Benchmark]
        public byte[] Current()
        {
            return Nethermind.Core.Encoding.Rlp.Encode(_address).Bytes;
        }
    }
}