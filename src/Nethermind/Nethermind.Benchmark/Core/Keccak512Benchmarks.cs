// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
//using Nethermind.HashLib;

namespace Nethermind.Benchmarks.Core
{
    public class Keccak512Benchmarks
    {
        //private static HashLib.Crypto.SHA3.Keccak512 _hash = HashFactory.Crypto.SHA3.CreateKeccak512();

        private byte[] _a;

        private byte[][] _scenarios =
        {
            new byte[]{},
            new byte[]{1},
            new byte[100000],
            TestItem.AddressA.Bytes
        };

        [Params(0, 1, 2, 3)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _a = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public byte[] Improved()
        {
            throw new NotImplementedException();
        }

        [Benchmark]
        public byte[] Current()
        {
            return Keccak512.Compute(_a).Bytes;
        }

        //[Benchmark]
        //public byte[] HashLib()
        //{
        //    return _hash.ComputeBytes(_a).GetBytes();
        //}
    }
}
