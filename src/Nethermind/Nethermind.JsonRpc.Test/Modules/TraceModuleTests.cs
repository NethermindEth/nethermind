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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Store;
using Nethermind.Store.Bloom;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Nethermind.Wallet;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class TraceModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            Initialize();
        }

        private void Initialize(bool auRa = false)
        {
            _ethSerializer = new EthereumJsonSerializer();
            foreach (JsonConverter jsonConverter in TraceModuleFactory.Converters)
            {
                _ethSerializer.RegisterConverter(jsonConverter);
            }
            
            _ethSerializer.RegisterConverter(new ParityTraceActionConverter());
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            IEthereumEcdsa ethereumEcdsa = new EthereumEcdsa(specProvider, LimboLogs.Instance);
            ITxStorage txStorage = new InMemoryTxStorage();
            _stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();
            IStateReader stateReader = new StateReader(_stateDb, codeDb, LimboLogs.Instance);
            _stateProvider = new StateProvider(_stateDb, codeDb, LimboLogs.Instance);
            _stateProvider.CreateAccount(TestItem.AddressA, 1000.Ether());
            _stateProvider.CreateAccount(TestItem.AddressB, 1000.Ether());
            _stateProvider.CreateAccount(TestItem.AddressC, 1000.Ether());
            byte[] code = Bytes.FromHexString("0xabcd");
            Keccak codeHash = Keccak.Compute(code);
            _stateProvider.UpdateCode(code);
            _stateProvider.UpdateCodeHash(TestItem.AddressA, codeHash, specProvider.GenesisSpec);

            IStorageProvider storageProvider = new StorageProvider(_stateDb, _stateProvider, LimboLogs.Instance);
            storageProvider.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));
            storageProvider.Commit();

            _stateProvider.Commit(specProvider.GenesisSpec);
            _stateProvider.CommitTree();

            ITxPool txPool = new TxPool.TxPool(txStorage, Timestamper.Default, ethereumEcdsa, specProvider, new TxPoolConfig(), _stateProvider, LimboLogs.Instance);

            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            IBlockTree blockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockDb), specProvider, txPool, NullBloomStorage.Instance, LimboLogs.Instance);

            IReceiptStorage receiptStorage = new InMemoryReceiptStorage();
            VirtualMachine virtualMachine = new VirtualMachine(_stateProvider, storageProvider, new BlockhashProvider(blockTree, LimboLogs.Instance), specProvider, LimboLogs.Instance);
            TransactionProcessor txProcessor = new TransactionProcessor(specProvider, _stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);
            IBlockProcessor blockProcessor = new BlockProcessor(specProvider, AlwaysValidBlockValidator.Instance, new RewardCalculator(specProvider), txProcessor, _stateDb, codeDb, _stateProvider, storageProvider, txPool, receiptStorage, LimboLogs.Instance);

            IFilterStore filterStore = new FilterStore();
            IFilterManager filterManager = new FilterManager(filterStore, blockProcessor, txPool, LimboLogs.Instance);
            _blockchainBridge = new BlockchainBridge(stateReader, _stateProvider, storageProvider, blockTree, txPool, receiptStorage, filterStore, filterManager, NullWallet.Instance, txProcessor, ethereumEcdsa, NullBloomStorage.Instance, LimboLogs.Instance, false);

            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockTree, blockProcessor, new TxSignaturesRecoveryStep(ethereumEcdsa, txPool, LimboLogs.Instance), LimboLogs.Instance, true);
            blockchainProcessor.Start();

            ManualResetEventSlim resetEvent = new ManualResetEventSlim(false);
            blockTree.NewHeadBlock += (s, e) =>
            {
                Console.WriteLine(e.Block.Header.Hash);
                if (e.Block.Number == 9)
                {
                    resetEvent.Set();
                }
            };

            var genesisBlockBuilder = Build.A.Block.Genesis.WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"));
            if (auRa)
            {
                genesisBlockBuilder.WithAura(0, new byte[65]);
            }

            Block genesis = genesisBlockBuilder.TestObject;
            blockTree.SuggestBlock(genesis);

            Block previousBlock = genesis;
            for (int i = 1; i < 10; i++)
            {
                List<Transaction> transactions = new List<Transaction>();
                for (int j = 0; j < i; j++)
                {
                    transactions.Add(Build.A.Transaction.WithNonce((UInt256)j).SignedAndResolved().TestObject);
                }
                
                BlockBuilder builder = Build.A.Block.WithNumber(i).WithParent(previousBlock).WithTransactions(transactions.ToArray()).WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"));
                if (auRa)
                {
                    builder.WithAura(i, i.ToByteArray());
                }

                Block block = builder.TestObject;
                blockTree.SuggestBlock(block);
                previousBlock = block;
            }

            IReceiptsRecovery receiptsRecovery = new ReceiptsRecovery();
            IReceiptFinder receiptFinder = new FullInfoReceiptFinder(receiptStorage, receiptsRecovery, blockTree);

            resetEvent.Wait(2000);
            _traceModule = new TraceModule(receiptFinder, new Tracer(_stateProvider, blockchainProcessor), _blockchainBridge);
            _blockTree = blockTree;
        }

        private IBlockchainBridge _blockchainBridge;
        private ITraceModule _traceModule;
        private IBlockTree _blockTree;
        private IJsonSerializer _ethSerializer;
        private IStateProvider _stateProvider;
        private ISnapshotableDb _stateDb;

        [Test]
        public void Tx_positions_are_fine()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters, _traceModule, "trace_block","latest");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x963e1762be217455aed852e2cbb46053ce0bca98\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x1fb701b713c746b25ac9b0b82345aef86c7541b001ee4c7be4922c71e66e073a\",\"transactionPosition\":0,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xaabfde31a679d9e83e35aaa0b952258e7fc8065f\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xd8b53c2348158e637009af277981ab3169ef8a8bff2a67fe52c2aaebe752ff58\",\"transactionPosition\":1,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x1c905b61b8683c09e07d2d591ff15d165b170e0a\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x4afaa014dcb4fd2fc7bd3af982277ab2f413d8ae7d907ad76afb59f3079fea43\",\"transactionPosition\":2,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xbeda477f0d0ace1ced214786397656d93c114918\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xeb27dfdd845ebbaa62d47b0950d6a38c43873539b71556e9381aeae4ba269f85\",\"transactionPosition\":3,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xacd3dcc14b31ac179d7f7164083c345fed149573\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xe4e9d78d3cf7eebb776da25f4fad66eb04a025801291dfffdf06ddd547d09498\",\"transactionPosition\":4,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xbc5ae1c3f5e65989c5664bac6a6de30314e82bb4\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x7256da5a198bc98aff7e4213e9e2a645e78bcde658dab20a9ab5d4a606c135cf\",\"transactionPosition\":5,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x8ea7891d652f94bbbdd105e572e1af30e6ae2822\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x89163ade4d1f29be8edc38a83403a7a889ebdf3cc9bd4bfe5254e7eabff57e4c\",\"transactionPosition\":6,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x5b4f11d5c61b499ff579f1ba6467a9aa5cba0c14\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xffc2757cbc0e467c287e757c6afcf33802ac4fc1bae7e322cd63dbdb04da3a57\",\"transactionPosition\":7,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x7b32782cdb74fc526f1000ba3781191ade11436f\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xbe9381d3d6f137e202ec390a2a024223e62e1abf2875b7227ca146a74df2e19c\",\"transactionPosition\":8,\"type\":null},{\"action\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"rewardType\":\"block\",\"value\":\"0x4563918244f40000\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }
    }
}