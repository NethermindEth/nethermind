﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class EthModuleTests
    {
        private TestRpcBlockchain _test;
        private TestRpcBlockchain _auraTest;

        [SetUp]
        public async Task SetUp()
        {
            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
            _auraTest = await TestRpcBlockchain.ForTest(SealEngineType.AuRa).Build();
        }

        [TestCase("earliest", "0x3635c9adc5dea00000")]
        [TestCase("latest", "0x3635c9adc5de9f09e5")]
        [TestCase("pending", "0x3635c9adc5de9f09e5")]
        [TestCase("0x0", "0x3635c9adc5dea00000")]
        public void Eth_get_balance(string blockParameter, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
            serialized.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}");
        }

        [Test]
        public void Eth_get_balance_default_block()
        {
            string serialized = _test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true));
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x3635c9adc5de9f09e5\",\"id\":67}}", serialized);
        }

        [Test]
        public void Eth_get_transaction_by_block_hash_and_index()
        {
            string serialized = _test.TestEthRpc("eth_getTransactionByBlockHashAndIndex", _test.BlockTree.FindHeadBlock().Hash.ToString(), "1");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public void Eth_get_transaction_by_hash()
        {
            string serialized = _test.TestEthRpc("eth_getTransactionByHash", _test.BlockTree.FindHeadBlock().Transactions.Last().Hash.ToString());
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public void Eth_pending_transactions()
        {
            _test.AddTransaction(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject);
            string serialized = _test.TestEthRpc("eth_pendingTransactions");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[{\"hash\":\"0x190d9a78dbc61b1856162ab909976a1b28ba4a41ee041341576ea69686cd3b29\",\"nonce\":\"0x0\",\"blockHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"blockNumber\":null,\"transactionIndex\":null,\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x26\",\"s\":\"0x2d04e55699fa32e6b65a22189f7571f5030d636d7d44a8b53fe016a2c3ecde24\",\"r\":\"0xda3978c3a1430bd902cf5bbca73c5a1eca019b3f003c95ee16657fd0bb89534c\"}],\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public void Eth_get_transaction_by_block_number_and_index()
        {
            string serialized = _test.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", _test.BlockTree.FindHeadBlock().Number.ToString(), "1");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Eth_get_uncle_by_block_number_and_index()
        {
            Block block = Build.A.Block.WithOmmers(Build.A.BlockHeader.TestObject, Build.A.BlockHeader.TestObject).TestObject;
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.FindBlock((BlockParameter) null).ReturnsForAnyArgs(block);
            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            string serialized = _test.TestEthRpc("eth_getUncleByBlockNumberAndIndex", _test.BlockTree.FindHeadBlock().Number.ToString(), "1");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Eth_get_uncle_by_block_hash_and_index()
        {
            Block block = Build.A.Block.WithOmmers(Build.A.BlockHeader.TestObject, Build.A.BlockHeader.TestObject).TestObject;
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.FindBlock((BlockParameter) null).ReturnsForAnyArgs(block);
            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            string serialized = _test.TestEthRpc("eth_getUncleByBlockHashAndIndex", _test.BlockTree.FindHeadBlock().Hash.ToString(), "1");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public void Eth_get_uncle_count_by_block_hash()
        {
            string serialized = _test.TestEthRpc("eth_getUncleCountByBlockHash", _test.BlockTree.FindHeadBlock().Hash.ToString());
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }
        
        [Test]
        public void Eth_get_uncle_count_by_block_number()
        {
            string serialized = _test.TestEthRpc("eth_getUncleCountByBlockNumber", _test.BlockTree.FindHeadBlock().Number.ToString());
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [TestCase("earliest", "0x0")]
        [TestCase("latest", "0x3")]
        [TestCase("pending", "0x3")]
        [TestCase("0x0", "0x0")]
        public void Eth_get_tx_count(string blockParameter, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getTransactionCount", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}", serialized);
        }

        [Test]
        public void Eth_get_tx_count_default_block()
        {
            string serialized = _test.TestEthRpc("eth_getTransactionCount", TestItem.AddressA.Bytes.ToHexString(true));
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x3\",\"id\":67}}", serialized);
        }
        
        [Test]
        public void Eth_get_filter_changes_empty()
        {
            string serialized1 = _test.TestEthRpc("eth_newBlockFilter");
            string serialized2 = _test.TestEthRpc("eth_getFilterChanges", "0");
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}}", serialized2);
        }
        
        [Test]
        public void Eth_uninstall_filter()
        {
            string serialized1 = _test.TestEthRpc("eth_newBlockFilter");
            string serialized2 = _test.TestEthRpc("eth_uninstallFilter", "0");
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}}", serialized2);
        }
        
        [Test]
        public async Task Eth_get_filter_changes_with_block()
        {
            string serialized1 = _test.TestEthRpc("eth_newBlockFilter");
            await _test.AddBlock();
            string serialized2 = _test.TestEthRpc("eth_getFilterChanges", "0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[\"0xac3e1ff2cf55e2f22da1d8e24ba2e9f8083f31b66730bf7bc1c1bb4cf29f9cd4\"],\"id\":67}", serialized2, serialized2.Replace("\"", "\\\""));
        }
        
        [Test]
        public void Eth_get_filter_changes_with_tx()
        {
            string serialized1 = _test.TestEthRpc("eth_newPendingTransactionFilter");
            _test.AddTransaction(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject);
            string serialized2 = _test.TestEthRpc("eth_getFilterChanges", "0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[\"0x190d9a78dbc61b1856162ab909976a1b28ba4a41ee041341576ea69686cd3b29\"],\"id\":67}", serialized2, serialized2.Replace("\"", "\\\""));
        }

        [TestCase("earliest", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
        [TestCase("latest", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
        [TestCase("pending", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
        [TestCase("0x0", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
        public void Eth_get_storage_at(string blockParameter, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1", blockParameter);
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}", serialized);
        }

        [Test]
        public void Eth_get_storage_at_default_block()
        {
            string serialized = _test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1");
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x0000000000000000000000000000000000000000000000000000000000abcdef\",\"id\":67}}", serialized);
        }

        [Test]
        public void Eth_get_block_number()
        {
            string serialized = _test.TestEthRpc("eth_blockNumber");
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x3\",\"id\":67}}", serialized);
        }

        [Test]
        public async Task Eth_get_balance_internal_error()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((Block) null);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            string serialized = _test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), "0x01");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Incorrect head block\"},\"id\":67}", serialized);
        }

        [Test]
        public void Eth_get_balance_incorrect_parameters()
        {
            string serialized = _test.TestEthRpc("eth_getBalance", TestItem.KeccakA.Bytes.ToHexString(true), "0x01");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\"},\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_syncing_true()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(false);
            bridge.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(900).TestObject).TestObject);
            bridge.BestKnown.Returns(1000L);
            bridge.IsSyncing.Returns(true);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();

            string serialized = _test.TestEthRpc("eth_syncing");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x384\",\"highestBlock\":\"0x3e8\"},\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_syncing_false()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(false);
            bridge.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(900).TestObject).TestObject);
            bridge.BestKnown.Returns(1000L);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            string serialized = _test.TestEthRpc("eth_syncing");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":false,\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_get_filter_logs()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.GetFilterLogs(Arg.Any<int>()).Returns(new[] {new FilterLog(1, 0, 1, TestItem.KeccakA, 1, TestItem.KeccakB, TestItem.AddressA, new byte[] {1, 2, 3}, new[] {TestItem.KeccakC, TestItem.KeccakD})});
            bridge.FilterExists(1).Returns(true);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            string serialized = _test.TestEthRpc("eth_getFilterLogs", "0x01");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"transactionLogIndex\":\"0x0\"}],\"id\":67}", serialized);
        }

        [TestCase("{}")]
        [TestCase("{\"fromBlock\":\"0x100\",\"toBlock\":\"latest\",\"address\":\"0x00000000000000000001\",\"topics\":[\"0x00000000000000000000000000000001\"]}")]
        [TestCase("{\"fromBlock\":\"earliest\",\"toBlock\":\"pending\",\"address\":[\"0x00000000000000000001\", \"0x00000000000000000001\"],\"topics\":[\"0x00000000000000000000000000000001\", \"0x00000000000000000000000000000002\"]}")]
        [TestCase("{\"topics\":[null, [\"0x00000000000000000000000000000001\", \"0x00000000000000000000000000000002\"]]}")]
        public async Task Eth_get_logs(string parameter)
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.GetLogs(Arg.Any<BlockParameter>(), Arg.Any<BlockParameter>(), Arg.Any<object>(), Arg.Any<IEnumerable<object>>()).Returns(new[] {new FilterLog(1, 0, 1, TestItem.KeccakA, 1, TestItem.KeccakB, TestItem.AddressA, new byte[] {1, 2, 3}, new[] {TestItem.KeccakC, TestItem.KeccakD})});
            bridge.FilterExists(1).Returns(true);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            string serialized = _test.TestEthRpc("eth_getLogs", parameter);

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"transactionLogIndex\":\"0x0\"}],\"id\":67}", serialized);
        }

        [Test]
        public void Eth_tx_count_by_hash()
        {
            string serialized = _test.TestEthRpc("eth_getBlockTransactionCountByHash", _test.BlockTree.Genesis.Hash.ToString());
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}", serialized);
        }

        [Test]
        public void Eth_uncle_count_by_hash()
        {
            string serialized = _test.TestEthRpc("eth_getUncleCountByBlockHash", _test.BlockTree.Genesis.Hash.ToString());
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}", serialized);
        }

        [TestCase("earliest", "\"0x0\"")]
        [TestCase("latest", "\"0x0\"")]
        [TestCase("pending", "\"0x0\"")]
        [TestCase("0x0", "\"0x0\"")]
        public void Eth_uncle_count_by_number(string blockParameter, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getUncleCountByBlockNumber", blockParameter);
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}", serialized);
        }

        [TestCase("earliest", "\"0x0\"")]
        [TestCase("latest", "\"0x2\"")]
        [TestCase("pending", "\"0x2\"")]
        [TestCase("0x0", "\"0x0\"")]
        public void Eth_tx_count_by_number(string blockParameter, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getBlockTransactionCountByNumber", blockParameter);
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}", serialized);
        }

        [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
        [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x16af125b31ba6f33725bffd77d8778121c8b24c3c29a9821d2fc15049a5bdcb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":\"0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"size\":\"0x21b\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":0,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
        public void Eth_get_block_by_hash(bool aura, string expected)
        {
            TestRpcBlockchain testBlockchain = (aura ? _auraTest : _test);
            string serialized = testBlockchain.TestEthRpc("eth_getBlockByHash", testBlockchain.BlockTree.Genesis.Hash.ToString(), "true");
            Assert.AreEqual(expected, serialized);
        }

        [Test]
        public void Eth_get_block_by_hash_null()
        {
            string serialized = _test.TestEthRpc("eth_getBlockByHash", Keccak.Zero.ToString(), "true");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}", serialized);
        }

        [TestCase("0x71eac5e72c3b64431c246173352a8c625c8434d944eb5f3f58204fec3ec36b54", false, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x65748e12d979898407962e1536a55f37293950ca7610fbcd09e5de7203d8299c\",\"receiptsRoot\":\"0x8512a59e92c89d12cf92d7e3a9e7abe96c52ba6ec2d7a0bd045dc4317c9b18fc\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x5dba6f8f6b72622a998cbf941635f19181686714c2467a3d77bd880ec339864c\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
        [TestCase("0x71eac5e72c3b64431c246173352a8c625c8434d944eb5f3f58204fec3ec36b54", true, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x65748e12d979898407962e1536a55f37293950ca7610fbcd09e5de7203d8299c\",\"receiptsRoot\":\"0x8512a59e92c89d12cf92d7e3a9e7abe96c52ba6ec2d7a0bd045dc4317c9b18fc\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x5dba6f8f6b72622a998cbf941635f19181686714c2467a3d77bd880ec339864c\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
        public void Eth_get_block_by_hash_with_tx(string blockParameter, bool withTxData, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getBlockByHash", _test.BlockTree.Head.Hash.ToString(), withTxData.ToString());
            Assert.AreEqual(expectedResult, serialized, serialized.Replace("\"", "\\\""));
        }

        [TestCase("earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
        [TestCase("latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x65748e12d979898407962e1536a55f37293950ca7610fbcd09e5de7203d8299c\",\"receiptsRoot\":\"0x8512a59e92c89d12cf92d7e3a9e7abe96c52ba6ec2d7a0bd045dc4317c9b18fc\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x5dba6f8f6b72622a998cbf941635f19181686714c2467a3d77bd880ec339864c\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
        [TestCase("pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x65748e12d979898407962e1536a55f37293950ca7610fbcd09e5de7203d8299c\",\"receiptsRoot\":\"0x8512a59e92c89d12cf92d7e3a9e7abe96c52ba6ec2d7a0bd045dc4317c9b18fc\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x5dba6f8f6b72622a998cbf941635f19181686714c2467a3d77bd880ec339864c\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
        [TestCase("0x0", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
        [TestCase("0x20", "{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}")]
        public void Eth_get_block_by_number(string blockParameter, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getBlockByNumber", blockParameter, "true");
            Assert.AreEqual(expectedResult, serialized, serialized.Replace("\"", "\\\""));
        }

        [TestCase("earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
        [TestCase("latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x65748e12d979898407962e1536a55f37293950ca7610fbcd09e5de7203d8299c\",\"receiptsRoot\":\"0x8512a59e92c89d12cf92d7e3a9e7abe96c52ba6ec2d7a0bd045dc4317c9b18fc\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x5dba6f8f6b72622a998cbf941635f19181686714c2467a3d77bd880ec339864c\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
        [TestCase("pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0xa194bd35b3fb3143f1010edd7823a85da1a6cf867a116748286682df19c69d43\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x65748e12d979898407962e1536a55f37293950ca7610fbcd09e5de7203d8299c\",\"receiptsRoot\":\"0x8512a59e92c89d12cf92d7e3a9e7abe96c52ba6ec2d7a0bd045dc4317c9b18fc\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x5dba6f8f6b72622a998cbf941635f19181686714c2467a3d77bd880ec339864c\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
        [TestCase("0x0", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
        [TestCase("0x20", "{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}")]
        public void Eth_get_block_by_number_no_details(string blockParameter, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getBlockByNumber", blockParameter, "false");
            Assert.AreEqual(expectedResult, serialized, serialized.Replace("\"", "\\\""));

            string serialized2 = _test.TestEthRpc("eth_getBlockByNumber", blockParameter);
            Assert.AreEqual(expectedResult, serialized2, serialized2);
        }

        [Test]
        public void Eth_get_block_by_number_null()
        {
            string serialized = _test.TestEthRpc("eth_getBlockByNumber", "1000000", "false");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}", serialized);
        }

        [Test]
        public void Eth_protocol_version()
        {
            string serialized = _test.TestEthRpc("eth_protocolVersion");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x41\",\"id\":67}", serialized);
        }

        [Test]
        public void Eth_get_code()
        {
            string serialized = _test.TestEthRpc("eth_getCode", TestItem.AddressA.ToString(), "latest");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0xabcd\",\"id\":67}", serialized);
        }

        [Test]
        public void Eth_get_code_default()
        {
            string serialized = _test.TestEthRpc("eth_getCode", TestItem.AddressA.ToString());
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0xabcd\",\"id\":67}", serialized);
        }

        [Test]
        public void Eth_call_web3_sample()
        {
            var transaction = _test.JsonSerializer.Deserialize<TransactionForRpc>("{\"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = _test.TestEthRpc("eth_call", _test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_mining_true()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsMining.Returns(true);
            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();

            string serialized = _test.TestEthRpc("eth_mining");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}", serialized);
        }

        [Test]
        public async Task Eth_mining_false()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsMining.Returns(false);
            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();

            string serialized = _test.TestEthRpc("eth_mining");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":false,\"id\":67}", serialized);
        }

        [Test]
        public void Eth_accounts()
        {
            string serialized = _test.TestEthRpc("eth_accounts");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[\"0x7e5f4552091a69125d5dfcb7b8c2659029395bdf\",\"0x2b5ad5c4795c026514f8317c7a215e218dccd6cf\",\"0x6813eb9362372eef6200f3b1dbc3f819671cba69\",\"0x1eff47bc3a10a45d4b230b5d10e37751fe6aa718\",\"0xe1ab8145f7e55dc933d51a18c793f901a3a0b276\",\"0xe57bfe9f44b819898f47bf37e5af72a0783e1141\",\"0xd41c057fd1c78805aac12b0a94a405c0461a6fbb\",\"0xf1f6619b38a98d6de0800f1defc0a6399eb6d30c\",\"0xf7edc8fa1ecc32967f827c9043fcae6ba73afa5c\",\"0x4cceba2d7d2b4fdce4304d3e09a1fea9fbeb1528\",\"0x3da8d322cb2435da26e9c9fee670f9fb7fe74e49\",\"0xdbc23ae43a150ff8884b02cea117b22d1c3b9796\",\"0x68e527780872cda0216ba0d8fbd58b67a5d5e351\",\"0x5a83529ff76ac5723a87008c4d9b436ad4ca7d28\",\"0x8735015837bd10e05d9cf5ea43a2486bf4be156f\",\"0xfae394561e33e242c551d15d4625309ea4c0b97f\",\"0x252dae0a4b9d9b80f504f6418acd2d364c0c59cd\",\"0x79196b90d1e952c5a43d4847caa08d50b967c34a\",\"0x4bd1280852cadb002734647305afc1db7ddd6acb\",\"0x811da72aca31e56f770fc33df0e45fd08720e157\",\"0x157bfbecd023fd6384dad2bded5dad7e27bf92e4\",\"0x37da28c050e3c0a1c0ac3be97913ec038783da4c\",\"0x3bc8287f1d872df4217283b7920d363f13cf39d8\",\"0xf4e2b0fcbd0dc4b326d8a52b718a7bb43bdbd072\",\"0x9a5279029e9a2d6e787c5a09cb068ab3d45e209d\",\"0xc39677f5f47d5fe65ab24e66750e8fca127c15be\",\"0x1dc728786e09f862e39be1f39dd218ee37feb68d\",\"0x636cc65783084b9f370789c90f733dbbeb88925d\",\"0x4a7a7c2e09209dbe44a582cd92b0edd7129e74be\",\"0xa56160a359f2eaa66f5c9df5245542b07339a9a6\",\"0x6b09d6433a379752157fd1a9e537c5cae5fa3168\",\"0x32e77de0d74a5c7af861aaed324c6a4c488142a8\",\"0x093d49d617a10f26915553255ec3fee532d2c12f\",\"0x138854708d8b603c9b7d4d6e55b6d32d40557f4d\",\"0x7dc0a40d64d72bb4590652b8f5c687bf7f26400c\",\"0x9358a525cc25aa571af0bcb5b98fbeab045a5e36\",\"0xd8e8ea89d71de89214fa39ba13ba9fcddc0d9467\",\"0xb56ed8f48979e1a948ad129199a600d0562cac51\",\"0xf65ac7003e905d72c666bfec1dc0960ecc9d0d6e\",\"0xd817d23c981472d703be36da777ffdb1abefd972\",\"0xf2adb90aa27a3c61a95c50063b20919d811e1476\",\"0xae3dffee97f92db0201d11cb8877c89738353bce\",\"0xeb3025e7ac2764040384316b33476e048961a71f\",\"0x9e3289708dc5709926a542fcf260fd4b210461f0\",\"0x6c23face014f20b3ebb65ae96d0d7ff32ab94c17\",\"0xb83b6241f966b1685c8b2ffce3956e21f35b4dcb\",\"0x6350872d7465864689def650443026f2f73a08da\",\"0x673c638147fe91e4277646d86d5ae82f775eea5c\",\"0xf472086186382fca55cd182de196520abd76f69d\",\"0x5ae58d2bc5145bff0c1bec0f32bfc2d079bc66ed\",\"0x2b29bea668b044b2b355c370f85b729bcb43ec40\",\"0x3797126345fb5fb6a37629db55ec692173cfb458\",\"0xe6869cc98283ab53e8a1a5312857ef0be9d189fe\",\"0xa5dfe354b3fc30c5c3a8ffefc8f9470d9177c334\",\"0xa1a625ae13b80a9c48b7c0331c83bc4541ac137f\",\"0xa33c9d26e1e33b84247defca631c1d30ffc76f5d\",\"0xf9807a8719ae154e74591cb2d452797707fadf73\",\"0xa1ba6fc3ea0e89f0e79f89d9aa0081d010571e4a\",\"0x366c20b40048556e5682e360997537c3715aca0e\",\"0xeb0e56f32246d043228fac8b63a71687d5199af1\",\"0xdb3ed822b78f0641623a12166607b5fa4df862ad\",\"0xb88c19426f03c6981d1a4281c7414d842b97619a\",\"0x32e04b012ac811c91d36a355a6d2859a0071a965\",\"0xe0dd44773f7657b11019062879d65f3d9862460c\",\"0x756be12856a8f44ab22fdbcbd42b70b843377d09\",\"0x6f4c950442e1af093bcff730381e63ae9171b87a\",\"0x4d1bf28514a4451249908e611366ec967c3d1558\",\"0xb0142d883494197b02c6ece84f571d81bd831124\",\"0x1326324f5a9fb193409e10006e4ea41b970df321\",\"0xf9a2c330a19e2fbfeb50fe7a7195b973bb0a3be9\",\"0x7a601ffa997cede6435aeabf4fa2091f09e149ec\",\"0xa92f4b5c4fddcc37e5139873ac28a4a0a42d68df\",\"0x850cc185d6cae4a7fdfb3dd81f977dd1df7d6503\",\"0xb1b7c87e8a0bf2e7fd1a1c582bd353e4c4529341\",\"0xff844fdb49e00776ad538db9ea2f9fa98ec0caf7\",\"0x1ac6f9601f2f616badcea8a0a307e1a3c14767a4\",\"0xc2aa6271409c10dee630e79df90c968989ccf2b7\",\"0x883d01eae6eaac077e126ddb32cd53550966ed76\",\"0x127688bbc070dd69a4db8c3ba5d43909e13d8f77\",\"0x0b54a50c0409dab2e63c3566324268ed53ec019a\",\"0xafd46e3549cc63d7a240d6177d056857679e6f99\",\"0x752481f35bb1d44d786c7b4dbe40db4a4266f96f\",\"0xac32def421e36b43629f785fd04523260e7f2b28\",\"0xfe6032a0810e90d025a3a39dd29844f964ee102c\",\"0x5cb6f3e6499d1f068b33351d0cae4b68cdf501bf\",\"0x84b743441b7bdf65cb4293126db4c1b709d7d95e\",\"0x8530a26f6c062f55597bd30c1a44e248decb0027\",\"0x5ce162cfa6208d7c50a7cb3525ac126155e7bce4\",\"0x2853dc9ca40d012969e25360cce0d9d326b24a86\",\"0x802271c02f76701929e1ea772e72783d28e4b60f\",\"0x7bd2aa0726ac3b9e752b120de8568e90b0423ae4\",\"0xb540c05d9b2516da9596a5ee75d750717a4be035\",\"0xa72392cd4be285ab6681da1bf1c45f0b370cb7b4\",\"0xcf484269182ac2388a4bfe6d19fb0687e3534b7f\",\"0x994907cb80bfd175f9b0b32672cfde0091368e2e\",\"0x36eab6ce7fededc098ef98c41e83548a89147131\",\"0x440db3ab842910d2a39f4e1be9e017c6823fb658\",\"0x25ac70ea6f44c4531a7117ea3620fa29cdaaca48\",\"0x24d881139ee639c2a774b4b1851cb7a9d0fce122\",\"0xd9a284367b6d3e25a91c91b5a430af2593886eb9\",\"0xe6b3367318c5e11a6eed3cd0d850ec06a02e9b90\",\"0x88c0e901bd1fd1a77bda342f0d2210fdc71cef6b\",\"0x7231c364597f3bfdb72cf52b197cc59111e71794\",\"0x043aed06383f290ee28fa02794ec7215ca099683\",\"0x0c95931d95694b3ef74071241827c09f25d40620\",\"0x417f3b59ef57c641283c2300fae0f27fe98d518c\",\"0xd6b931d8d441b1ec98f55f8ec8adb532dc140c78\",\"0x9220625b1a30680387d542e6b5f753786ca5530e\",\"0x997cf669860a1dcc76344866534d8679a7b562e2\",\"0xb961768b578514debf079017ff78c47b0a6adbf6\",\"0x052b91ad9732d1bce0ddae15a4545e5c65d02443\",\"0x8df64de79608f0ae9e72ecae3a400582aed8101c\",\"0x0e7b23cd1fdb7ea3ccc80320ab43843a2f193c36\",\"0xfbbc41289f834a76e4320ac238512035560467ee\",\"0x61e1da6c7b8b211e6e5dd921efe27e73ad226dac\",\"0x87fcbe64187317c59a944be5b9c5c830b9373730\",\"0x2acf0d6fdac920081a446868b2f09a8dac797448\",\"0x1715eb68afba4d516ef1e068b55f5093bb4a2f59\",\"0x58bab2f728dc4fc227a4c38cab2ec93b73b4e828\",\"0x25346934b4faa00dee0190c2069156bde6010c18\",\"0xa01cca6367a84304b6607b76676c66c360b74741\",\"0x872917cec8992487651ee633dba73bd3a9dca309\",\"0x6c1a01c2ab554930a937b0a2e8105fb47946c679\",\"0x13c0e7c715fdea35c7f9663c719e4d36601275b9\",\"0xe8c5025c803f3279d94ea55598e147f601929bf4\",\"0x639acdbd838b81cea8d6a970136812783fa5bf5e\",\"0xb3087f34edab33a8182ba29adea4d739d9831a94\",\"0xc6a210606f2ee6e64afb9584db054f3476a5cc66\",\"0xd01c9d93efc83c00b30f768f832182beff65696f\",\"0x00edf2d16afbc028fb1e879559b07997af79539f\",\"0xf5d722030d17ca01f2813af9e7be158d7a037997\",\"0xae3d43ab6fdcd35386db427099ff11aa670ee0f4\",\"0x0dc8b8ef8457b1e45ac277d65ac5987b547ba775\",\"0xde521346f9327a4314a18b2cda96b2a33603177a\",\"0x69842e12d6f36f9f93f06086b70795bfc7e02745\",\"0x9b7bdf6ad17d5fc9a168acaa24495e52a65f3b79\",\"0xa2d47d2c42009520075cb15f5855052008d0c44d\",\"0xb0c249f6f92fb2491fc9750a5299d856ba2ea3c6\",\"0x839d96957f21e82fbcca0d42a1f12ef6e1cf82e9\",\"0x2a0d6b92b042497013e5549d6579202608ce0c80\",\"0xa4f8c598927eab2f1898f8f2d6f8121578de2344\",\"0xdb21655b672dacc8da6f538c899f9d6969604117\",\"0x21289cd01f9f58fc44962b6e213a0fbbd015beb6\",\"0x0b62d63c314d94dfa85b11a9c652ffe438382d6c\",\"0x9383e3096133f464d516b518b12851fd10d891f4\",\"0x64e582c17ab7c3b90e171795b504ca3c04108501\",\"0x848406919d014b1e5c27a82f951caff840fd63ef\",\"0x5fe015779fb36006b01f9c5a5dbcaa6ffa56f0c0\",\"0x28b6e15f86025b8ea8beaa6855a81069bfb6ab1e\",\"0x271d65af9a5a7b4cd7af264f251184c2a4b9e7a3\",\"0xddf44e34ed40c40624c7b9f20a1030b505a4fac0\",\"0xe5854075272ca5ef71663d5b87e0cd5ac53b2f36\",\"0x2798ba84d7830c5f60d750f37f87d93277106905\",\"0x7e9961fa09dd52f945f8143844785cf0e51bb4ce\",\"0xf33d2f7d96f92d912ca8418f9d62eb54c1a9889f\",\"0xeec566c793a89f388bbabfc0225183a6a95c4263\",\"0x2001f8cdcdeef1bbcc188ca59cf04fb44133d55a\",\"0x3bf958fa0626e898f548a8f95cf9ab3a4db65169\",\"0xb0d744fde06bbcb6655eb55288ec94fa6a0b2a52\",\"0x18eb36d090eeadf82f3454a6da690fc398d3eba1\",\"0xd2431ca38735c2fd438e2caa23f094191d89675b\",\"0x612b7be154a64292aae070aaa86fcd66ba218071\",\"0x681ce2f439fdc80e70c1eea8b8a085dfb976d32a\",\"0x2174ca3ee9ace7dd8c946c97054c72f2b384c4c2\",\"0x1d694d5ad94f32132ff5c14c901d3ddbee90a550\",\"0x0b6fe046e6fd8d7a7a36d5ad1ffb82d2e3e5c3bb\",\"0x258f4ed0560e290a95066d9dee3628f2f179302b\",\"0xe2a09565167d4e3f826adec6bef82b97e0a4383f\",\"0x9af70704e9ec5f505cdba564ff4dec03503ddaa2\",\"0xeb9afe072c781401bf364224c75a036e4d832f52\",\"0x07748403082b29a45abd6c124a37e6b14e6b1803\",\"0x63486b70d804464766cfd096bba5552c4bcdac30\",\"0x5181be40152caaba8e123a55b7762755d4e8e416\",\"0x9481da7766c043eefeecc9589ee7ade61316b0ff\",\"0x42aba3530dd1ccb1dda27bfaa7c6a832cfdb4446\",\"0x05650444ace15a01762bd97ee8fdeb495b3c2436\",\"0xd83d18a2eae2440e272a53f86e617cd9f33c8d68\",\"0x4a35a802dbd623561040dd50f6293842d0901731\",\"0x4dbaf6c348d8cd1f174a7a6155f80ea8d4a8baf8\",\"0x9efc4e49be8ff70d596ac20efec9b7842e1ea963\",\"0x68efde0cdd917c6da6dab02c23f69e7c9cff51a9\",\"0x99b52813933a46d95bd4265ea2f674e58827da97\",\"0x7b35461cc5adbdc415c1f9562ccc342adbf09bd4\",\"0x8ee8813fb9d41cc58ef87d28b36e948b1234e71d\",\"0x69c1bc7883a7bb7696c7726d025867cd16564c9c\",\"0x31eb18dd6f5a8064ab750eabb281cf162f43ccd0\",\"0xf5d122e123d9d7998d2bea685d11b10fec3e4508\",\"0xf762854586a40a93d1fdcde32c062829f3754de9\",\"0x1e3f8fb9f840325983d6e5c68b6b846ff66a20ac\",\"0x3c1638a25ad7e8c2a84b53b661dd1bd048407e8f\",\"0x2eedf8a799b73bc02e4664183eb72422c377153b\",\"0xcdef6f23a26f960b53468f497967ce74c026af52\",\"0x0a2035683fe5587b0b09db0e757306ad729fe6c1\",\"0x158cc083cfbcffb2f983a3aa8b027eb0711c9831\",\"0x691cb1645a4f21d879973b3a3b98a714fc1970d6\",\"0x754164c0cb85dda1b5b18e5b62adbb4d60c3efbf\",\"0x556330e8d92912ccf133851ba03abd2db70da404\",\"0x1745ceba112b0a41638e235ec59b35adf37b70ab\",\"0xa24c85b16a440587793f82e358fa6b204468735b\",\"0x5304fb08724d73f2bb5e04c582407c33cde6c8d3\",\"0x256a11785fc43141324cf61efb5f491378c10c85\",\"0xa9f161a2badd44f3fe45b91a044a9484b72f1dc4\",\"0xd5cc10c45fc0f9f956acd7559f61edbfec9f6c3d\",\"0x381c7a71035bdb42fb5d77523df2ff00d9f9df1b\",\"0x45cdbeea730d8212f451a6a8d0eb5998b04cccca\",\"0x6367283f25a32be0c28623d787c319e237c3b7bd\",\"0x598e94eb5e050045272d8417f6ab363bd874d568\",\"0x379ff6375f4a44f458683629ef05141d73fae55a\",\"0x18df8ba2ef19083ddff68f8b33976cf22e8419c6\",\"0xffceebc37a7351d5df9aa3b077ed39cc3b5192ff\",\"0x1cd21f00b58894260f7abed65ad23dce3cea0226\",\"0x26324733d604abb6176cf18e4f4a0624ceeddc09\",\"0x4102d394d723ff141b82ef9a6053fb89f90c67f4\",\"0x269c370cb95b63f9b6a7cad47998167f160a2689\",\"0x3bb9557113fbb052dae3008a2801a072c432c018\",\"0x3f588a72d94d0d0986b112c671c2343320a19386\",\"0x7cfa9eee1d752da599211bc8a68d0687708dabfc\",\"0x7bc23966c419eadcb8a2fc5f83e635c4d4ad0c2f\",\"0xc4ad60337b04fc721912531a52a5d77878293fb9\",\"0xfc5ba3041f750f9b6820ce066c153eb396aac1ff\",\"0x32480c2d857941d2fff4e34f0910b20c0f9c23bc\",\"0x8041c9a96585053db2d7214b5de56828645b8e62\",\"0x444ca66b3ceb4187840cb1a205566a1413d5fecb\",\"0xf084bbaabee1a700a8faa404027db620a5aa0059\",\"0x602d562b4ef2544f851587619b56f77a9d965d45\",\"0x216faec139a61329ef8b31d982de427d9c007a9e\",\"0x11eb17b20113ae923d72e52870d40bf59a08b40d\",\"0xe69017fcc36bbc7fb167b9585bdd47a950ba1992\",\"0xe5549f429a72bfa618cf5c1afdac22a730df6a1a\",\"0x161c2e10407e2a87959c0bae1f342c80eaea28f3\",\"0x4161220db043a7d682e0ad123a3f8fea165711aa\",\"0xb33609811fb3d9fc8955dc6e9e086f1f08fc3a65\",\"0x4148555ea4c00e14f81ef399bbe67ef2fd9811b1\",\"0x4f81e991f76276a17ca92a1321f37189b1727f77\",\"0xba95e317ead06b55c8b70276fc63904b3339dfa1\",\"0xf6203c4fb14da640d11fbd9573e3958d017e6745\",\"0x73377d6228266393747efa710017872d6dd5b9a6\",\"0xf7862d105fc6ee69604decc30aa89472ad405961\",\"0xfa1205e19719c248323563bd55cd8bfd08b0cbc6\",\"0x4f46630115b446f8f7cebe1e5961ef7858c25204\",\"0x7492ebbc1e7f2838fc7191edc031581d5712978a\",\"0xc0af3981f9c0dfcb8955fea07a3e4f23806fab51\",\"0x8621dd642245df371b584b48c081e8863313a70d\",\"0xc328de035c91b39efa07d2fe620813253c9b4ec2\",\"0xa11308e3b741227d41973ddb17534ceb27b8206f\",\"0xc4ff1b4565ee203fa12636e100fe9c89cd18acb7\",\"0x63a36aea8570219476ef835f09024acdedfee95a\",\"0xf7205066c153f7c88dc3653ebc72b438884ae109\",\"0xa8ce5c40c4aa9278ddeaa418e775985549960e7a\",\"0x81f58f2194b0413806bf2ce8e1654bc855dc65a1\",\"0xf0218008120201e66b65fce4df9035007e811228\",\"0x90f022e3ca8453f5e5765cd3054003b544526eec\",\"0x1d1f873ba1ddf7915e6e26f93f5624b40efaefe2\",\"0x0311afd3bc2ae250d5f9f2706bae2ef4164d6912\",\"0x5044a80bd3eff58302e638018534bbda8896c48a\"],\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public void Eth_call_web3_sample_not_enough_gas_system_account()
        {
            _test.State.AccountExists(Address.SystemUser).Should().BeFalse();
            var transaction = _test.JsonSerializer.Deserialize<TransactionForRpc>("{\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = _test.TestEthRpc("eth_call", _test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
            _test.State.AccountExists(Address.SystemUser).Should().BeFalse();
        }

        [Test]
        public void Eth_call_web3_sample_not_enough_gas_other_account()
        {
            Address someAccount = new Address("0x0001020304050607080910111213141516171819");
            _test.State.AccountExists(someAccount).Should().BeFalse();
            var transaction = _test.JsonSerializer.Deserialize<TransactionForRpc>("{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = _test.TestEthRpc("eth_call", _test.JsonSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
            _test.State.AccountExists(someAccount).Should().BeFalse();
        }

        [Test]
        public void Eth_estimateGas_web3_sample_not_enough_gas_system_account()
        {
            _test.State.AccountExists(Address.SystemUser).Should().BeFalse();
            var transaction = _test.JsonSerializer.Deserialize<TransactionForRpc>("{\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = _test.TestEthRpc("eth_estimateGas", _test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x5898\",\"id\":67}", serialized);
            _test.State.AccountExists(Address.SystemUser).Should().BeFalse();
        }

        [Test]
        public void Eth_estimateGas_web3_sample_not_enough_gas_other_account()
        {
            Address someAccount = new Address("0x0001020304050607080910111213141516171819");
            _test.State.AccountExists(someAccount).Should().BeFalse();
            var transaction = _test.JsonSerializer.Deserialize<TransactionForRpc>("{\"from\":\"0x0001020304050607080910111213141516171819\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = _test.TestEthRpc("eth_estimateGas", _test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x5898\",\"id\":67}", serialized);
            _test.State.AccountExists(someAccount).Should().BeFalse();
        }

        [Test]
        public void Eth_estimateGas_web3_above_block_gas_limit()
        {
            Address someAccount = new Address("0x0001020304050607080910111213141516171819");
            _test.State.AccountExists(someAccount).Should().BeFalse();
            var transaction = _test.JsonSerializer.Deserialize<TransactionForRpc>("{\"from\":\"0x0001020304050607080910111213141516171819\",\"gas\":\"0x100000000\",\"gasPrice\":\"0x100000\", \"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = _test.TestEthRpc("eth_estimateGas", _test.JsonSerializer.Serialize(transaction));
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x5898\",\"id\":67}", serialized);
            _test.State.AccountExists(someAccount).Should().BeFalse();
        }

        [Test]
        public void Eth_call_no_sender()
        {
            var transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.To = TestItem.AddressB;

            string serialized = _test.TestEthRpc("eth_call", _test.JsonSerializer.Serialize(transaction), "latest");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public void Eth_call_no_recipient_should_work_as_init()
        {
            var transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.From = TestItem.AddressA;
            transaction.Data = new byte[] {1, 2, 3};

            string serialized = _test.TestEthRpc("eth_call", _test.JsonSerializer.Serialize(transaction), "latest");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32015,\"message\":\"VM execution error.\",\"data\":\"StackUnderflow\"},\"id\":67}", serialized);
        }

        [Test]
        public void Eth_call_ethereum_recipient()
        {
            string serialized = _test.TestEthRpc("eth_call", "{\"data\":\"0x12\",\"from\":\"0x7301cfa0e1756b71869e93d4e4dca5c7d0eb0aa6\",\"to\":\"ethereum\"}", "latest");
            Assert.True(serialized.StartsWith("{\"jsonrpc\":\"2.0\",\"error\""));
        }

        [Test]
        public void Eth_call_ok()
        {
            var transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.From = TestItem.AddressA;
            transaction.To = TestItem.AddressB;

            string serialized = _test.TestEthRpc("eth_call", _test.JsonSerializer.Serialize(transaction), "latest");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}", serialized);
        }

        [Test]
        public void Eth_call_missing_state_after_fast_sync()
        {
            var transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.From = TestItem.AddressA;
            transaction.To = TestItem.AddressB;

            _test.StateDb.Clear();

            string serialized = _test.TestEthRpc("eth_call", _test.JsonSerializer.Serialize(transaction), "latest");
            serialized.Should().StartWith("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,");
        }

        [Test]
        public void Eth_get_block_by_number_with_number_bad_number()
        {
            string serialized = _test.TestEthRpc("eth_getBlockByNumber", "'0x1234567890123456789012345678901234567890123456789012345678901234567890'", "true");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\"},\"id\":67}", serialized);
        }

        [Test]
        public void Eth_get_proof()
        {
            string serialized = _test.TestEthRpc("eth_getProof", TestBlockchain.AccountA.ToString(), "[]", "0x2");
            Assert.AreEqual(serialized, "{\"jsonrpc\":\"2.0\",\"result\":{\"accountProof\":[\"0xf8518080808080a0670d0cd172a8244835ccc1499687a1fadc0603097339c9055dd664ab916774c48080808080a053692ab7cdc9bb02a28b1f45afe7be86cb27041ea98586e6ff05d98c9b0667138080808080\",\"0xf871808080a007d973b7753725c09ed04da1e01a73dfa6ae1922d8027d42ab55977cc4bdea3580a00dd1727b2abb59c0a6ac75c01176a9d1a276b0049d5fe32da3e1551096549e258080808080808080a038ca33d3070331da1ccf804819da57fcfc83358cadbef1d8bde89e1a346de5098080\",\"0xf872a020227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5eb84ff84d01893635c9adc5de9fadf7a0475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72ba0dbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\"],\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"balance\":\"0x3635c9adc5de9fadf7\",\"codeHash\":\"0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\",\"nonce\":\"0x1\",\"storageHash\":\"0x475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72b\",\"storageProof\":[]},\"id\":67}", serialized.Replace("\"", "\\\""));
        }

        [Test]
        public void Eth_get_block_by_number_empty_param()
        {
            string serialized = _test.TestEthRpc("eth_getBlockByNumber", "", "true");
            Assert.True(serialized.StartsWith("{\"jsonrpc\":\"2.0\",\"error\""));
        }

        [Test]
        public async Task Eth_get_transaction_receipt()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            var entries = new[]
            {
                Build.A.LogEntry.TestObject,
                Build.A.LogEntry.TestObject
            };
            bridge.GetReceipt(Arg.Any<Keccak>()).Returns(Build.A.Receipt.WithBloom(new Bloom(entries, new Bloom())).WithAllFieldsFilled.WithLogs(entries).TestObject);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            string serialized = _test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA.ToString());

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"status\":\"0x1\",\"error\":\"error\"},\"id\":67}", serialized);
        }

        [Test]
        public void Eth_get_transaction_receipt_returns_null_on_missing_receipt()
        {
            string serialized = _test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA.ToString());
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}", serialized);
        }


        [Test]
        public async Task Eth_syncing()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(true);
            bridge.BestKnown.Returns(6178000L);
            bridge.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6170000L).TestObject).TestObject);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            string serialized = _test.TestEthRpc("eth_syncing");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x5e2590\",\"highestBlock\":\"0x5e44d0\"},\"id\":67}", serialized);
        }

        [Test]
        public void Eth_chain_id()
        {
            string serialized = _test.TestEthRpc("eth_chainid");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x1\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Send_transaction_with_signature_will_not_try_to_sign()
        {
            ITxPoolBridge txPoolBridge = Substitute.For<ITxPoolBridge>();
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            txPoolBridge.SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast).Returns(TestItem.KeccakA);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).WithTxPoolBridge(txPoolBridge).Build();
            Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            string serialized = _test.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(tx, RlpBehaviors.None).Bytes.ToHexString());

            bridge.DidNotReceiveWithAnyArgs().Sign(null);
            txPoolBridge.Received().SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast);
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"{TestItem.KeccakA.Bytes.ToHexString(true)}\",\"id\":67}}", serialized);
        }
    }
}