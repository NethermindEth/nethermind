// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Rlp
{
    /// <inheritdoc cref="RlpDecodeReceiptBenchmark"/>
    public class RlpDecodeAccountBenchmark
    {
        private const int Batch = 256;

        private byte[] _account;

        private readonly byte[][] _scenarios =
        [
            Serialization.Rlp.Rlp.Encode(Account.TotallyEmpty).Bytes,
            Serialization.Rlp.Rlp.Encode(
                new Account(123, UInt256.Parse("1000000000000000000000", NumberStyles.HexNumber))).Bytes,
        ];

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup() => _account = _scenarios[ScenarioIndex];

        [Benchmark(OperationsPerInvoke = Batch)]
        public Account Current()
        {
            Account account = null;
            for (int i = 0; i < Batch; i++)
            {
                account = Serialization.Rlp.Rlp.Decode<Account>(_account);
            }

            return account;
        }
    }
}
