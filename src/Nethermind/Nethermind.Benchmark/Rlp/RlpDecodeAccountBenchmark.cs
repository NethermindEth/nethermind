// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeAccountBenchmark
    {
        private static byte[] _account;

        private byte[][] _scenarios =
        {
            Serialization.Rlp.Rlp.Encode(Account.TotallyEmpty).Bytes,
            Serialization.Rlp.Rlp.Encode(Build.An.Account.WithBalance(UInt256.Parse("0x1000000000000000000000", NumberStyles.HexNumber)).WithNonce(123).TestObject).Bytes,
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
            return Serialization.Rlp.Rlp.Decode<Account>(_account);
        }

        [Benchmark]
        public Account Current()
        {
            return Serialization.Rlp.Rlp.Decode<Account>(_account);
        }
    }
}
