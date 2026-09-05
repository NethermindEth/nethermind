// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeAccountBenchmark
    {
        private static byte[] _account;

        // Built directly: AccountBuilder.WithBalance narrows to ulong, so it cannot carry the
        // wide balance this benchmark exists to exercise.
        private byte[][] _scenarios =
        {
            Serialization.Rlp.Rlp.Encode(Account.TotallyEmpty).Bytes,
            Serialization.Rlp.Rlp.Encode(
                new Account(123, UInt256.Parse("1000000000000000000000", NumberStyles.HexNumber))).Bytes,
        };

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup() => _account = _scenarios[ScenarioIndex];

        [Benchmark]
        public Account Improved() => Serialization.Rlp.Rlp.Decode<Account>(_account);

        [Benchmark]
        public Account Current() => Serialization.Rlp.Rlp.Decode<Account>(_account);
    }
}
