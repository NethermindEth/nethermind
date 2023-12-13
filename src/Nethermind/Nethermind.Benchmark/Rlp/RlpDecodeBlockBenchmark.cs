// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeBlockBenchmark
    {
        private static byte[] _block;

        private byte[][] _scenarios;

        public RlpDecodeBlockBenchmark()
        {
            var transactions = new Transaction[100];
            for (int i = 0; i < 100; i++)
            {
                transactions[i] = Build.A.Transaction.WithData(new byte[] { (byte)i }).WithNonce((UInt256)i).WithValue((UInt256)i).Signed(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            }

            _scenarios = new[]
            {
                Serialization.Rlp.Rlp.Encode(Build.A.Block.WithNumber(1).TestObject).Bytes,
                Serialization.Rlp.Rlp.Encode(Build.A.Block.WithNumber(1).WithTransactions(transactions).WithUncles(Build.A.BlockHeader.TestObject).WithMixHash(Keccak.EmptyTreeHash).TestObject).Bytes
            };
        }

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _block = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public Block Improved()
        {
            return Serialization.Rlp.Rlp.Decode<Block>(_block);
        }

        [Benchmark]
        public Block Current()
        {
            return Serialization.Rlp.Rlp.Decode<Block>(_block);
        }
    }
}
