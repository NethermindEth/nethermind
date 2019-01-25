/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Blockchain.Filters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class EthModuleTests
    {
        [SetUp]
        public void Initialize()
        {
        }

        [Test]
        public void Eth_get_balance()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.FindBlock(Arg.Any<UInt256>()).Returns(Build.A.Block.TestObject);
            bridge.GetAccount(Arg.Any<Address>(), Arg.Any<Keccak>()).Returns(Build.A.Account.WithBalance(1.Ether()).TestObject);
            bridge.Head.Returns(Build.A.BlockHeader.TestObject);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.AddressA.Bytes.ToHexString(true), "0x01");

            Assert.AreEqual( "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0xde0b6b3a7640000\"}", serialized);
        }
        
        [Test]
        public void Eth_get_block_number()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber(310000).TestObject);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_blockNumber");

            Assert.AreEqual( $"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x4baf0\"}}", serialized);
        }

        [Test]
        public void Eth_get_balance_internal_error()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((BlockHeader) null);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.AddressA.Bytes.ToHexString(true), "0x01");

            Assert.AreEqual( "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"error\":{\"code\":-32603,\"message\":\"Incorrect head block\",\"data\":null}}", serialized);
        }

        [Test]
        public void Eth_get_balance_incorrect_number_of_params()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((BlockHeader) null);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.AddressA.Bytes.ToHexString(true));

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"error\":{\"code\":-32602,\"message\":\"Incorrect parameters count, expected: 2, actual: 1\",\"data\":null}}", serialized);
        }

        [Test]
        public void Eth_get_balance_incorrect_parameters()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((BlockHeader) null);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBalance", TestObject.KeccakA.Bytes.ToHexString(true), "0x01");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"error\":{\"code\":-32602,\"message\":\"Incorrect parameters\",\"data\":null}}", serialized);
        }

        [Test]
        public void Eth_syncing_true()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(false);
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber(900).TestObject);
            bridge.BestKnown.Returns((UInt256) 1000);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_syncing");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x384\",\"highestBlock\":\"0x3e8\"}}", serialized);
        }

        [Test]
        [Ignore("We always return true")]
        [Todo(Improve.MissingFunctionality, "Add correct reporting of not syncing")]
        public void Eth_syncing_false()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(true);
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber(900).TestObject);
            bridge.BestKnown.Returns((UInt256) 1000);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_syncing");

            Assert.AreEqual( "{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":false}", serialized);
        }

        [Test]
        public void Eth_get_filter_logs()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.GetFilterLogs(Arg.Any<int>()).Returns(new[] {new FilterLog(1, 1, TestObject.KeccakA, 1, TestObject.KeccakB, TestObject.AddressA, new byte[] {1, 2, 3}, new[] {TestObject.KeccakC, TestObject.KeccakD})});
            bridge.FilterExists(1).Returns(true);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getFilterLogs", "0x01");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":[{\"removed\":false,\"logIndex\":\"0x1\",\"blockNumber\":\"0x1\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"data\":\"0x010203\",\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"]}]}", serialized);
        }

        [Test]
        public void Eth_get_block_by_hash()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.FindBlock(Arg.Any<Keccak>(), Arg.Any<bool>()).Returns(Build.A.Block.WithTotalDifficulty(0).WithTransactions(Build.A.Transaction.TestObject).TestObject);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBlockByHash", TestObject.KeccakA.ToString(), "true");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"number\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"nonce\":\"0x3e8\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"totalDifficulty\":\"0x0\",\"extraData\":\"0x010203\",\"size\":\"0x0\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[{\"hash\":null,\"nonce\":\"0x0\",\"blockHash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"blockNumber\":\"0x0\",\"transactionIndex\":\"0x0\",\"from\":null,\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\"}],\"transactionHashes\":null,\"uncles\":[]}}", serialized);
        }

        [Test]
        public void Eth_get_block_by_number()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.FindBlock(Arg.Any<Keccak>(), Arg.Any<bool>()).Returns(Build.A.Block.WithTotalDifficulty(0).WithTransactions(Build.A.Transaction.TestObject).TestObject);
            bridge.RetrieveHeadBlock().Returns(Build.A.Block.WithTotalDifficulty(0).WithTransactions(Build.A.Transaction.TestObject).TestObject);
            bridge.Head.Returns(Build.A.BlockHeader.TestObject);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBlockByNumber", "latest", "true");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"number\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"nonce\":\"0x3e8\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"totalDifficulty\":\"0x0\",\"extraData\":\"0x010203\",\"size\":\"0x0\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[{\"hash\":null,\"nonce\":\"0x0\",\"blockHash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"blockNumber\":\"0x0\",\"transactionIndex\":\"0x0\",\"from\":null,\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\"}],\"transactionHashes\":null,\"uncles\":[]}}", serialized);
        }

        [Test]
        public void Eth_get_block_by_number_empty_param()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.FindBlock(Arg.Any<Keccak>(), Arg.Any<bool>()).Returns(Build.A.Block.WithTotalDifficulty(0).WithTransactions(Build.A.Transaction.TestObject).TestObject);
            bridge.RetrieveHeadBlock().Returns(Build.A.Block.WithTotalDifficulty(0).WithTransactions(Build.A.Transaction.TestObject).TestObject);
            bridge.Head.Returns(Build.A.BlockHeader.TestObject);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getBlockByNumber", "", "true");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"number\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"nonce\":\"0x3e8\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"totalDifficulty\":\"0x0\",\"extraData\":\"0x010203\",\"size\":\"0x0\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[{\"hash\":null,\"nonce\":\"0x0\",\"blockHash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"blockNumber\":\"0x0\",\"transactionIndex\":\"0x0\",\"from\":null,\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\"}],\"transactionHashes\":null,\"uncles\":[]}}", serialized);
        }

        [Test]
        public void Eth_get_transaction_receipt()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            var entries = new[]
            {
                Build.A.LogEntry.TestObject,
                Build.A.LogEntry.TestObject
            };
            bridge.GetTransactionReceipt(Arg.Any<Keccak>()).Returns(Build.A.Receipt.WithBloom(new Bloom(entries)).WithAllFieldsFilled.WithLogs(entries).TestObject);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getTransactionReceipt", TestObject.KeccakA.ToString());

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"status\":\"0x0\",\"error\":\"error\"}}", serialized);
        }
        
        [Test]
        public void Eth_get_transaction_receipt_returns_null_on_missing_receipt()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            
            bridge.GetTransactionReceipt(Arg.Any<Keccak>()).Returns((TransactionReceipt)null);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_getTransactionReceipt", TestObject.KeccakA.ToString());

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null}", serialized);
        }


        [Test]
        public void Eth_syncing()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.BestKnown.Returns((UInt256) 6178000);
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber((UInt256) 6170000).TestObject);

            IEthModule module = new EthModule(new UnforgivingJsonSerializer(), Substitute.For<IConfigProvider>(), NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(module, "eth_syncing");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x5e2590\",\"highestBlock\":\"0x5e44d0\"}}", serialized);
        }
    }
}