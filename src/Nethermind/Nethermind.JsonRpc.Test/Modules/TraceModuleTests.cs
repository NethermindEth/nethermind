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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
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
using Nethermind.Db.Blooms;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Nethermind.Wallet;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
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
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            IEthereumEcdsa ethereumEcdsa = new EthereumEcdsa(specProvider.ChainId, LimboLogs.Instance);
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
            IBlockProcessor blockProcessor = new BlockProcessor(specProvider, Always.Valid, new RewardCalculator(specProvider), txProcessor, _stateDb, codeDb, _stateProvider, storageProvider, txPool, receiptStorage, LimboLogs.Instance);

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
        private IStateProvider _stateProvider;
        private ISnapshotableDb _stateDb;

        [Test]
        public void Tx_positions_are_fine()
        {
            string serialized = RpcTest.TestSerializedRequest(EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), _traceModule, "trace_block","latest");
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x723847c97bc651c7e8c013dbbe65a70712f02ad3\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x1fb701b713c746b25ac9b0b82345aef86c7541b001ee4c7be4922c71e66e073a\",\"transactionPosition\":0,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xd1f6f7a9c6abafb69ee7964a8e2c57f305a85d50\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xd8b53c2348158e637009af277981ab3169ef8a8bff2a67fe52c2aaebe752ff58\",\"transactionPosition\":1,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x58e52229bbd8470b4323248eaa3e8d6dbedc1c4b\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x4afaa014dcb4fd2fc7bd3af982277ab2f413d8ae7d907ad76afb59f3079fea43\",\"transactionPosition\":2,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x32e3aa7a76512638020fbd76682770444783150b\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xeb27dfdd845ebbaa62d47b0950d6a38c43873539b71556e9381aeae4ba269f85\",\"transactionPosition\":3,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xbadf2a39f1373c41d0acd2b72e109de7154137f8\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xe4e9d78d3cf7eebb776da25f4fad66eb04a025801291dfffdf06ddd547d09498\",\"transactionPosition\":4,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x90b071326047073a0ec2677fd189e67d6a0ce533\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x7256da5a198bc98aff7e4213e9e2a645e78bcde658dab20a9ab5d4a606c135cf\",\"transactionPosition\":5,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xc7199565e1b3939b3668c304e0ee251c8aa29415\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x89163ade4d1f29be8edc38a83403a7a889ebdf3cc9bd4bfe5254e7eabff57e4c\",\"transactionPosition\":6,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xd1acdb7a39fdb41e5b674b8e483663a69d5e9d92\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xffc2757cbc0e467c287e757c6afcf33802ac4fc1bae7e322cd63dbdb04da3a57\",\"transactionPosition\":7,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x86ef57e31aa45d3dcb35d044297b5e22f1ae62e7\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xbe9381d3d6f137e202ec390a2a024223e62e1abf2875b7227ca146a74df2e19c\",\"transactionPosition\":8,\"type\":null},{\"action\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"rewardType\":\"block\",\"value\":\"0x4563918244f40000\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }
    }
}