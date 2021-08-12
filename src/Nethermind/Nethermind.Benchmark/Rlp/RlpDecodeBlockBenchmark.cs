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

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;

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
                transactions[i] = Build.A.Transaction.WithData(new byte[] {(byte) i}).WithNonce((UInt256) i).WithValue((UInt256) i).Signed(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            }

            _scenarios = new[]
            {
                Serialization.Rlp.Rlp.Encode(Build.A.Block.WithNumber(1).TestObject).Bytes,
                Serialization.Rlp.Rlp.Encode(Build.A.Block.WithNumber(1).WithTransactions(transactions).WithOmmers(Build.A.BlockHeader.TestObject).WithMixHash(Keccak.EmptyTreeHash).TestObject).Bytes
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
