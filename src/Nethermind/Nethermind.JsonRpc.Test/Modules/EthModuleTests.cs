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

using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Nethermind.Wallet;
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
            Rlp.RegisterDecoders(typeof(ParityTraceDecoder).Assembly);

            _ethSerializer = new EthereumJsonSerializer();
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            IEthereumEcdsa ethereumEcdsa = new EthereumEcdsa(specProvider, LimboLogs.Instance);
            ITxStorage txStorage = new InMemoryTxStorage();
            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();
            IStateReader stateReader = new StateReader(stateDb, codeDb, LimboLogs.Instance);
            IStateProvider stateProvider = new StateProvider(stateDb, codeDb, LimboLogs.Instance);
            stateProvider.CreateAccount(TestItem.AddressA, 1000.Ether());
            stateProvider.CreateAccount(TestItem.AddressB, 1000.Ether());
            stateProvider.CreateAccount(TestItem.AddressC, 1000.Ether());
            byte[] code = Bytes.FromHexString("0xabcd");
            Keccak codeHash = Keccak.Compute(code);
            stateProvider.UpdateCode(code);
            stateProvider.UpdateCodeHash(TestItem.AddressA, codeHash, specProvider.GenesisSpec);

            IStorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, LimboLogs.Instance);
            storageProvider.Set(new StorageAddress(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));
            storageProvider.Commit();

            stateProvider.Commit(specProvider.GenesisSpec);

            ITxPool txPool = new TxPool(txStorage, Timestamper.Default, ethereumEcdsa, specProvider, new TxPoolConfig(), stateProvider, LimboLogs.Instance);

            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            IDb traceDb = new MemDb();
            IBlockTree blockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockDb), specProvider, txPool, LimboLogs.Instance);

            IReceiptStorage receiptStorage = new InMemoryReceiptStorage();
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, new BlockhashProvider(blockTree, LimboLogs.Instance), specProvider, LimboLogs.Instance);
            TransactionProcessor txProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);
            IBlockProcessor blockProcessor = new BlockProcessor(specProvider, AlwaysValidBlockValidator.Instance, new RewardCalculator(specProvider), txProcessor, stateDb, codeDb, traceDb, stateProvider, storageProvider, txPool, receiptStorage, LimboLogs.Instance);

            IFilterStore filterStore = new FilterStore();
            IFilterManager filterManager = new FilterManager(filterStore, blockProcessor, txPool, LimboLogs.Instance);
            _blockchainBridge = new BlockchainBridge(stateReader, stateProvider, storageProvider, blockTree, txPool, receiptStorage, filterStore, filterManager, NullWallet.Instance, txProcessor, ethereumEcdsa);

            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockTree, blockProcessor, new TxSignaturesRecoveryStep(ethereumEcdsa, txPool, LimboLogs.Instance), LimboLogs.Instance, true, true);
            blockchainProcessor.Start();

            ManualResetEventSlim resetEvent = new ManualResetEventSlim(false);
            blockTree.NewHeadBlock += (s, e) =>
            {
                if (e.Block.Number == 9)
                {
                    resetEvent.Set();
                }
            };

            Block genesis = Build.A.Block.Genesis.WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f")).TestObject;
            blockTree.SuggestBlock(genesis);

            Block previousBlock = genesis;
            for (int i = 1; i < 10; i++)
            {
                Block block = Build.A.Block.WithNumber(i).WithParent(previousBlock).WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f")).TestObject;
                blockTree.SuggestBlock(block);
                previousBlock = block;
            }

            resetEvent.Wait();
            _ethModule = new EthModule(LimboLogs.Instance, _blockchainBridge);
            _blockTree = blockTree;
        }

        private IBlockchainBridge _blockchainBridge;
        private IEthModule _ethModule;
        private IBlockTree _blockTree;
        private IJsonSerializer _ethSerializer;

        [TestCase("earliest", "0x3635c9adc5dea00000")]
        [TestCase("latest", "0x3635c9adc5dea00000")]
        [TestCase("pending", "0x3635c9adc5dea00000")]
        [TestCase("0x0", "0x3635c9adc5dea00000")]
        public void Eth_get_balance(string blockParameter, string expectedResult)
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\"}}", serialized);
        }
        
        [Test]
        public void Eth_get_balance_default_block()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true));
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x3635c9adc5dea00000\"}}", serialized);
        }

        [TestCase("earliest", "0x0")]
        [TestCase("latest", "0x0")]
        [TestCase("pending", "0x0")]
        [TestCase("0x0", "0x0")]
        public void Eth_get_tx_count(string blockParameter, string expectedResult)
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getTransactionCount", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\"}}", serialized);
        }
        
        [Test]
        public void Eth_get_tx_count_default_block()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getTransactionCount", TestItem.AddressA.Bytes.ToHexString(true));
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x0\"}}", serialized);
        }

        [TestCase("earliest", "0xabcdef")]
        [TestCase("latest", "0xabcdef")]
        [TestCase("pending", "0xabcdef")]
        [TestCase("0x0", "0xabcdef")]
        public void Eth_get_storage_at(string blockParameter, string expectedResult)
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1", blockParameter);
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\"}}", serialized);
        }
        
        [Test]
        public void Eth_get_storage_at_default_block()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1");
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0xabcdef\"}}", serialized);
        }

        [Test]
        public void Eth_get_block_number()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_blockNumber");
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x9\"}}", serialized);
        }

        [Test]
        public void Eth_get_balance_internal_error()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.Head.Returns((BlockHeader) null);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), "0x01");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Incorrect head block\",\"data\":null}}", serialized);
        }

        [Test]
        public void Eth_get_balance_incorrect_parameters()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBalance", TestItem.KeccakA.Bytes.ToHexString(true), "0x01");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Incorrect parameters\",\"data\":null}}", serialized);
        }

        [Test]
        public void Eth_syncing_true()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(false);
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber(900).TestObject);
            bridge.BestKnown.Returns(1000L);
            bridge.IsSyncing.Returns(true);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_syncing");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x384\",\"highestBlock\":\"0x3e8\"}}", serialized);
        }

        [Test]
        public void Eth_syncing_false()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(false);
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber(900).TestObject);
            bridge.BestKnown.Returns(1000L);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_syncing");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":false}", serialized);
        }

        [Test]
        public void Eth_get_filter_logs()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.GetFilterLogs(Arg.Any<int>()).Returns(new[] {new FilterLog(1, 1, TestItem.KeccakA, 1, TestItem.KeccakB, TestItem.AddressA, new byte[] {1, 2, 3}, new[] {TestItem.KeccakC, TestItem.KeccakD})});
            bridge.FilterExists(1).Returns(true);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_getFilterLogs", "0x01");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":[{\"removed\":false,\"logIndex\":\"0x1\",\"blockNumber\":\"0x1\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"data\":\"0x010203\",\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"]}]}", serialized);
        }

        [TestCase("{}")]
        [TestCase("{\"fromBlock\":\"0x100\",\"toBlock\":\"latest\",\"address\":\"0x00000000000000000001\",\"topics\":[\"0x00000000000000000000000000000001\"]}")]
        [TestCase("{\"fromBlock\":\"earliest\",\"toBlock\":\"pending\",\"address\":[\"0x00000000000000000001\", \"0x00000000000000000001\"],\"topics\":[\"0x00000000000000000000000000000001\", \"0x00000000000000000000000000000002\"]}")]
        [TestCase("{\"topics\":[null, [\"0x00000000000000000000000000000001\", \"0x00000000000000000000000000000002\"]]}")]
        public void Eth_get_logs(string parameter)
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.GetLogs(Arg.Any<FilterBlock>(), Arg.Any<FilterBlock>(), Arg.Any<object>(), Arg.Any<IEnumerable<object>>()).Returns(new[] {new FilterLog(1, 1, TestItem.KeccakA, 1, TestItem.KeccakB, TestItem.AddressA, new byte[] {1, 2, 3}, new[] {TestItem.KeccakC, TestItem.KeccakD})});
            bridge.FilterExists(1).Returns(true);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_getLogs", parameter);

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":[{\"removed\":false,\"logIndex\":\"0x1\",\"blockNumber\":\"0x1\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"data\":\"0x010203\",\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"]}]}", serialized);
        }

        [Test]
        public void Eth_tx_count_by_hash()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBlockTransactionCountByHash", _blockTree.Genesis.Hash.ToString());
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x0\"}", serialized);
        }

        [Test]
        public void Eth_uncle_count_by_hash()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getUncleCountByBlockHash", _blockTree.Genesis.Hash.ToString());
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x0\"}", serialized);
        }

        [TestCase("earliest", "\"0x0\"")]
        [TestCase("latest", "\"0x0\"")]
        [TestCase("pending", "\"0x0\"")]
        [TestCase("0x0", "\"0x0\"")]
        public void Eth_uncle_count_by_number(string blockParameter, string expectedResult)
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getUncleCountByBlockNumber", blockParameter);
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{expectedResult}}}", serialized);
        }

        [TestCase("earliest", "\"0x0\"")]
        [TestCase("latest", "\"0x0\"")]
        [TestCase("pending", "\"0x0\"")]
        [TestCase("0x0", "\"0x0\"")]
        public void Eth_tx_count_by_number(string blockParameter, string expectedResult)
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBlockTransactionCountByNumber", blockParameter);
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{expectedResult}}}", serialized);
        }

        [Test]
        public void Eth_get_block_by_hash()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBlockByHash", _blockTree.Genesis.Hash.ToString(), "true");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}}", serialized);
        }

        [Test]
        public void Eth_get_block_by_hash_null()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBlockByHash", Keccak.Zero.ToString(), "true");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null}", serialized);
        }

        [TestCase("earliest", "{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}")]
        [TestCase("latest", "{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x4db333d5856e844f0083582b2d6911992f266e36f8eaa4c9b86f44f5a38c570d\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x9\",\"parentHash\":\"0x1dfed2c3d8f83385db5ed0d4176dee47461037aa32301292a2e4bb21d3d94aba\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0x989680\",\"timestamp\":\"0xf4249\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}")]
        [TestCase("pending", "{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x4db333d5856e844f0083582b2d6911992f266e36f8eaa4c9b86f44f5a38c570d\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x9\",\"parentHash\":\"0x1dfed2c3d8f83385db5ed0d4176dee47461037aa32301292a2e4bb21d3d94aba\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0x989680\",\"timestamp\":\"0xf4249\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}")]
        [TestCase("0x0", "{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}")]
        [TestCase("0x20", "null")]
        public void Eth_get_block_by_number(string blockParameter, string expectedResult)
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBlockByNumber", blockParameter, "true");
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{expectedResult}}}", serialized);
        }

        [TestCase("earliest", "{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}")]
        [TestCase("latest", "{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x4db333d5856e844f0083582b2d6911992f266e36f8eaa4c9b86f44f5a38c570d\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x9\",\"parentHash\":\"0x1dfed2c3d8f83385db5ed0d4176dee47461037aa32301292a2e4bb21d3d94aba\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0x989680\",\"timestamp\":\"0xf4249\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}")]
        [TestCase("pending", "{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x4db333d5856e844f0083582b2d6911992f266e36f8eaa4c9b86f44f5a38c570d\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x9\",\"parentHash\":\"0x1dfed2c3d8f83385db5ed0d4176dee47461037aa32301292a2e4bb21d3d94aba\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0x989680\",\"timestamp\":\"0xf4249\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}")]
        [TestCase("0x0", "{\"author\":null,\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":null,\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":null,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}")]
        [TestCase("0x20", "null")]
        public void Eth_get_block_by_number_no_details(string blockParameter, string expectedResult)
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBlockByNumber", blockParameter, "false");
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{expectedResult}}}", serialized);
        }

        [Test]
        public void Eth_get_code()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getCode", TestItem.AddressA.ToString(), "latest");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0xabcd\"}", serialized);
        }
        
        [Test]
        public void Eth_get_code_default()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getCode", TestItem.AddressA.ToString());
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0xabcd\"}", serialized);
        }

        [Test]
        public void Eth_call_web3_sample()
        {
            var transaction = _ethSerializer.Deserialize<TransactionForRpc>("{\"data\": \"0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160\", \"to\": \"0x0d8775f648430679a709e98d2b0cb6250d2887ef\"}");
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_call", _ethSerializer.Serialize(transaction), "0x0");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x\"}", serialized);
        }

        [Test]
        public void Eth_call_no_sender()
        {
            var transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.To = TestItem.AddressB;

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_call", _ethSerializer.Serialize(transaction), "latest");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x\"}", serialized);
        }

        [Test]
        public void Eth_call_no_recipient()
        {
            var transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.From = TestItem.AddressA;

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_call", _ethSerializer.Serialize(transaction), "latest");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Recipient address not specified on the transaction.\",\"data\":null}}", serialized);
        }

        [Test]
        public void Eth_call_ok()
        {
            var transaction = new TransactionForRpc(Keccak.Zero, 1L, 1, new Transaction());
            transaction.From = TestItem.AddressA;
            transaction.To = TestItem.AddressB;

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_call", _ethSerializer.Serialize(transaction), "latest");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x\"}", serialized);
        }

        [Test]
        public void Eth_get_block_by_number_with_number_bad_number()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBlockByNumber", "'0x1234567890123456789012345678901234567890123456789012345678901234567890'", "true");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Incorrect parameters\",\"data\":null}}", serialized);
        }

        [Test]
        public void Eth_get_block_by_number_empty_param()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _ethModule, "eth_getBlockByNumber", "", "true");
            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Incorrect parameters count, expected: 2, actual: 1\",\"data\":null}}", serialized);
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
            bridge.GetReceipt(Arg.Any<Keccak>()).Returns(Build.A.Receipt.WithBloom(new Bloom(entries, new Bloom())).WithAllFieldsFilled.WithLogs(entries).TestObject);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_getTransactionReceipt", TestItem.KeccakA.ToString());

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"status\":\"0x1\",\"error\":\"error\"}}", serialized);
        }

        [Test]
        public void Eth_get_transaction_receipt_returns_null_on_missing_receipt()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();

            bridge.GetReceipt(Arg.Any<Keccak>()).Returns((TxReceipt) null);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_getTransactionReceipt", TestItem.KeccakA.ToString());

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":null}", serialized);
        }


        [Test]
        public void Eth_syncing()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.IsSyncing.Returns(true);
            bridge.BestKnown.Returns(6178000L);
            bridge.Head.Returns(Build.A.BlockHeader.WithNumber(6170000L).TestObject);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_syncing");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x5e2590\",\"highestBlock\":\"0x5e44d0\"}}", serialized);
        }

        [Test]
        public void Eth_chain_id()
        {
            IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
            bridge.GetChainId().Returns(1);

            IEthModule module = new EthModule(NullLogManager.Instance, bridge);

            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, module, "eth_chainid");

            Assert.AreEqual("{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x1\"}", serialized);
        }
    }
}