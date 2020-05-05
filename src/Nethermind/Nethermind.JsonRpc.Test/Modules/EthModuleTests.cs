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

        [TestCase("earliest", "0xabcdef")]
        [TestCase("latest", "0xabcdef")]
        [TestCase("pending", "0xabcdef")]
        [TestCase("0x0", "0xabcdef")]
        public void Eth_get_storage_at(string blockParameter, string expectedResult)
        {
            string serialized = _test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1", blockParameter);
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}", serialized);
        }

        [Test]
        public void Eth_get_storage_at_default_block()
        {
            string serialized = _test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1");
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"0xabcdef\",\"id\":67}}", serialized);
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
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}", serialized);
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
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();

            bridge.GetReceipt(Arg.Any<Keccak>()).Returns((TxReceipt) null);

            IEthModule module = new EthModule(new JsonRpcConfig(), bridge, LimboLogs.Instance);

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
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.GetChainId().Returns(1);

            IEthModule module = new EthModule(new JsonRpcConfig(), bridge, LimboLogs.Instance);

            string serialized = _test.TestEthRpc("eth_chainid");

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":\"0x1\",\"id\":67}", serialized);
        }

        [Test]
        public async Task Send_transaction_with_signature_will_not_try_to_sign()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.SendTransaction(null, TxHandlingOptions.PersistentBroadcast).ReturnsForAnyArgs(TestItem.KeccakA);

            _test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
            Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance), TestItem.PrivateKeyA, 10000000).TestObject;
            string serialized = _test.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(tx, RlpBehaviors.None).Bytes.ToHexString());

            bridge.DidNotReceiveWithAnyArgs().Sign(null);
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"{TestItem.KeccakA.Bytes.ToHexString(true)}\",\"id\":67}}", serialized);
        }
    }
}