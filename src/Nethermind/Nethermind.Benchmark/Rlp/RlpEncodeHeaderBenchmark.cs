// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpEncodeHeaderBenchmark
    {
        private static HeaderDecoder _headerDecoder = new HeaderDecoder();

        private static BlockHeader _header;

        private BlockHeader[] _scenarios;

        public RlpEncodeHeaderBenchmark()
        {
            var transactions = new Transaction[100];
            for (int i = 0; i < 100; i++)
            {
                transactions[i] = Build.A.Transaction.WithData(new byte[] { (byte)i }).WithNonce((UInt256)i).WithValue((UInt256)i).Signed(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            }

            _scenarios = new[]
            {
                Build.A.BlockHeader.WithNumber(1).TestObject,
            };
        }

        [Params(0)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _header = _scenarios[ScenarioIndex];

            Console.WriteLine($"Length current: {Current().Length}");
            Console.WriteLine($"Length improved: {Improved().Length}");
            Console.WriteLine($"Length improved2: {Improved2().Length}");
            Check(Current(), Improved());
            Check(Current(), Improved2());
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
        public byte[] Improved2()
        {
            return _headerDecoder.Encode(_header).Bytes;
        }

        [Benchmark]
        public byte[] Improved()
        {
            throw new NotImplementedException();
        }

        [Benchmark(Baseline = true)]
        public byte[] Current()
        {
            return Serialization.Rlp.Rlp.Encode(_header).Bytes;
        }
    }
}
