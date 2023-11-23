// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using BenchmarkDotNet.Attributes;
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
