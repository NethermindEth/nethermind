//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
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
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using Nethermind.Blockchain.Find;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Json;
using Nethermind.Trie.Pruning;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class TraceRpcModuleTests
    {
        private class Context
        {
            private readonly IBlockTree _blockTree;
            AutoResetEvent resetEvent = new(false);

            public Context(bool auRa = false)
            {
                IDbProvider dbProvider = TestMemDbProvider.Init();
                ISpecProvider specProvider = MainnetSpecProvider.Instance;
                JsonRpcConfig = new JsonRpcConfig();
                IEthereumEcdsa ethereumEcdsa = new EthereumEcdsa(specProvider.ChainId, LimboLogs.Instance);

                MemDb stateDb = new MemDb();
                ITrieStore trieStore = new TrieStore(stateDb, LimboLogs.Instance);
                MemDb codeDb = new();
                IStateProvider stateProvider = new StateProvider(trieStore, codeDb, LimboLogs.Instance);
                IStateReader stateReader = new StateReader(trieStore, codeDb, LimboLogs.Instance);

                stateProvider.CreateAccount(TestItem.AddressA, 1000.Ether());
                stateProvider.CreateAccount(TestItem.AddressB, 1000.Ether());
                stateProvider.CreateAccount(TestItem.AddressC, 1000.Ether());
                byte[] code = Bytes.FromHexString("0xabcd");
                Keccak codeHash = Keccak.Compute(code);
                stateProvider.UpdateCode(code);
                stateProvider.UpdateCodeHash(TestItem.AddressA, codeHash, specProvider.GenesisSpec);

                IStorageProvider storageProvider = new StorageProvider(trieStore, stateProvider, LimboLogs.Instance);
                storageProvider.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));
                storageProvider.Commit();

                stateProvider.Commit(specProvider.GenesisSpec);
                stateProvider.CommitTree(0);

                IChainLevelInfoRepository chainLevels = new ChainLevelInfoRepository(dbProvider);
                _blockTree = new BlockTree(dbProvider, chainLevels, specProvider, NullBloomStorage.Instance,
                    LimboLogs.Instance);
                ITransactionComparerProvider transactionComparerProvider =
                    new TransactionComparerProvider(specProvider, _blockTree);
                ITxPool txPool = new TxPool.TxPool(ethereumEcdsa,
                    new ChainHeadInfoProvider(specProvider, _blockTree, stateReader),
                    new TxPoolConfig(), new TxValidator(specProvider.ChainId), LimboLogs.Instance,
                    transactionComparerProvider.GetDefaultComparer());

                IReceiptStorage receiptStorage = new InMemoryReceiptStorage();
                VirtualMachine virtualMachine = new(stateProvider, storageProvider,
                    new BlockhashProvider(_blockTree, LimboLogs.Instance), specProvider, LimboLogs.Instance);
                TransactionProcessor txProcessor = new(specProvider, stateProvider, storageProvider, virtualMachine,
                    LimboLogs.Instance);
                IBlockProcessor blockProcessor = new BlockProcessor(
                    specProvider,
                    Always.Valid,
                    new RewardCalculator(specProvider),
                    new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, stateProvider),
                    stateProvider,
                    storageProvider,
                    receiptStorage,
                    NullWitnessCollector.Instance,
                    LimboLogs.Instance);

                RecoverSignatures signatureRecovery = new(ethereumEcdsa, txPool, specProvider, LimboLogs.Instance);
                BlockchainProcessor blockchainProcessor = new(_blockTree, blockProcessor, signatureRecovery,
                    LimboLogs.Instance, BlockchainProcessor.Options.Default);

                blockchainProcessor.Start();
                _blockTree.NewHeadBlock += (s, e) =>
                {
                    if (e.Block!.Number >= 9)
                        resetEvent.Set();
                };

                BlockBuilder genesisBlockBuilder =
                    Build.A.Block.Genesis.WithStateRoot(
                        new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"));
                if (auRa)
                {
                    genesisBlockBuilder.WithAura(0, new byte[65]);
                }

                Block genesis = genesisBlockBuilder.TestObject;
                _blockTree.SuggestBlock(genesis);

                Block previousBlock = genesis;
                for (int i = 1; i < 10; i++)
                {
                    List<Transaction> transactions = new();
                    for (int j = 0; j < i; j++)
                    {
                        transactions.Add(Build.A.Transaction.WithNonce((UInt256)j).SignedAndResolved().TestObject);
                    }

                    BlockBuilder builder = Build.A.Block.WithNumber(i).WithParent(previousBlock)
                        .WithTransactions(transactions.ToArray()).WithStateRoot(
                            new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"));
                    if (auRa)
                    {
                        builder.WithAura(i, i.ToByteArray());
                    }

                    Block block = builder.TestObject;
                    _blockTree.SuggestBlock(block);
                    previousBlock = block;
                }

                ReceiptsRecovery receiptsRecovery =
                    new(new EthereumEcdsa(specProvider.ChainId, LimboLogs.Instance), specProvider);
                IReceiptFinder receiptFinder = new FullInfoReceiptFinder(receiptStorage, receiptsRecovery, _blockTree);

                resetEvent.WaitOne();

                TraceRpcModule = new TraceRpcModule(receiptFinder, new Tracer(stateProvider, blockchainProcessor),
                    _blockTree, JsonRpcConfig, MainnetSpecProvider.Instance, LimboLogs.Instance);
            }

            public void BuildNewBlock(Transaction[] transactions)
            {
                Block? currentHead = _blockTree.Head;
                Block block = Build.A.Block.WithParent(currentHead!).WithNumber(currentHead!.Number + 1)
                    .WithTransactions(transactions)
                    .WithStateRoot(
                        new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
                    .TestObject;
                _blockTree.SuggestBlock(block);
                resetEvent.WaitOne();
            }

            public ITraceRpcModule TraceRpcModule { get; }

            public IJsonRpcConfig JsonRpcConfig { get; }
        }

        [Test]
        public void Tx_positions_are_fine()
        {
            Context context = new();
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_block", "latest");
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x723847c97bc651c7e8c013dbbe65a70712f02ad3\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x1fb701b713c746b25ac9b0b82345aef86c7541b001ee4c7be4922c71e66e073a\",\"transactionPosition\":0,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xd1f6f7a9c6abafb69ee7964a8e2c57f305a85d50\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xd8b53c2348158e637009af277981ab3169ef8a8bff2a67fe52c2aaebe752ff58\",\"transactionPosition\":1,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x58e52229bbd8470b4323248eaa3e8d6dbedc1c4b\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x4afaa014dcb4fd2fc7bd3af982277ab2f413d8ae7d907ad76afb59f3079fea43\",\"transactionPosition\":2,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x32e3aa7a76512638020fbd76682770444783150b\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xeb27dfdd845ebbaa62d47b0950d6a38c43873539b71556e9381aeae4ba269f85\",\"transactionPosition\":3,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xbadf2a39f1373c41d0acd2b72e109de7154137f8\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xe4e9d78d3cf7eebb776da25f4fad66eb04a025801291dfffdf06ddd547d09498\",\"transactionPosition\":4,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x90b071326047073a0ec2677fd189e67d6a0ce533\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x7256da5a198bc98aff7e4213e9e2a645e78bcde658dab20a9ab5d4a606c135cf\",\"transactionPosition\":5,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xc7199565e1b3939b3668c304e0ee251c8aa29415\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x89163ade4d1f29be8edc38a83403a7a889ebdf3cc9bd4bfe5254e7eabff57e4c\",\"transactionPosition\":6,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xd1acdb7a39fdb41e5b674b8e483663a69d5e9d92\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xffc2757cbc0e467c287e757c6afcf33802ac4fc1bae7e322cd63dbdb04da3a57\",\"transactionPosition\":7,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x86ef57e31aa45d3dcb35d044297b5e22f1ae62e7\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xbe9381d3d6f137e202ec390a2a024223e62e1abf2875b7227ca146a74df2e19c\",\"transactionPosition\":8,\"type\":null},{\"action\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"rewardType\":\"block\",\"value\":\"0x4563918244f40000\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"id\":67}",
                serialized, serialized.Replace("\"", "\\\""));
        }
        
        [Test]
        public void Trace_filter_return_fail_with_not_existing_block()
        {
            Context context = new();
            string request = "{\"fromBlock\":\"0x154\",\"after\":0}";
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_filter", request);

            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Block 340 could not be found\"},\"id\":67}",
                serialized, serialized.Replace("\"", "\\\""));
        }
        
        [Test]
        public void Trace_filter_return_fail_from_block_higher_than_to_block()
        {
            Context context = new();
            string request = "{\"fromBlock\":\"0x8\",\"toBlock\":\"0x6\"}";
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_filter", request);

            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"From block number: 8 is greater than to block number 6\"},\"id\":67}",
                serialized, serialized.Replace("\"", "\\\""));
        }
        
        [Test]
        public void Trace_filter_return_empty_result_with_count_0()
        {
            Context context = new();
            string request = "{\"count\":0x0, \"fromBlock\":\"0x3\",\"toBlock\":\"0x3\"}";
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_filter", request);

            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}",
                serialized, serialized.Replace("\"", "\\\""));
        }
        

        [Test]
        public void Trace_filter_return_expected_json()
        {
            Context context = new();
            TraceFilterForRpc traceFilterRequest = new();
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_filter", new EthereumJsonSerializer().Serialize(traceFilterRequest));

            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x723847c97bc651c7e8c013dbbe65a70712f02ad3\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x1fb701b713c746b25ac9b0b82345aef86c7541b001ee4c7be4922c71e66e073a\",\"transactionPosition\":0,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xd1f6f7a9c6abafb69ee7964a8e2c57f305a85d50\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xd8b53c2348158e637009af277981ab3169ef8a8bff2a67fe52c2aaebe752ff58\",\"transactionPosition\":1,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x58e52229bbd8470b4323248eaa3e8d6dbedc1c4b\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x4afaa014dcb4fd2fc7bd3af982277ab2f413d8ae7d907ad76afb59f3079fea43\",\"transactionPosition\":2,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x32e3aa7a76512638020fbd76682770444783150b\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xeb27dfdd845ebbaa62d47b0950d6a38c43873539b71556e9381aeae4ba269f85\",\"transactionPosition\":3,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xbadf2a39f1373c41d0acd2b72e109de7154137f8\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xe4e9d78d3cf7eebb776da25f4fad66eb04a025801291dfffdf06ddd547d09498\",\"transactionPosition\":4,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0x90b071326047073a0ec2677fd189e67d6a0ce533\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x7256da5a198bc98aff7e4213e9e2a645e78bcde658dab20a9ab5d4a606c135cf\",\"transactionPosition\":5,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xc7199565e1b3939b3668c304e0ee251c8aa29415\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0x89163ade4d1f29be8edc38a83403a7a889ebdf3cc9bd4bfe5254e7eabff57e4c\",\"transactionPosition\":6,\"type\":null},{\"action\":{\"callType\":\"call\",\"from\":\"0xd1acdb7a39fdb41e5b674b8e483663a69d5e9d92\",\"gas\":\"0x5208\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"result\":{\"gasUsed\":\"0x0\",\"output\":null},\"subtraces\":0,\"transactionHash\":\"0xffc2757cbc0e467c287e757c6afcf33802ac4fc1bae7e322cd63dbdb04da3a57\",\"transactionPosition\":7,\"type\":null},{\"action\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"rewardType\":\"block\",\"value\":\"0x4563918244f40000\"},\"blockHash\":\"0x7a4597196e0e3c1e6c843bcdad49a0946b2096b1817f49eca911627748950d8b\",\"blockNumber\":9,\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xbe9381d3d6f137e202ec390a2a024223e62e1abf2875b7227ca146a74df2e19c\",\"transactionPosition\":8,\"type\":\"reward\"}],\"id\":67}",
                serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public void Trace_filter_skip_expected_number_of_traces()
        {
            Context context = new();
            TraceFilterForRpc traceRequest = new();
            traceRequest.After = 3;
            ResultWrapper<ParityTxTraceFromStore[]> secondTraces = context.TraceRpcModule.trace_filter(traceRequest);
            Assert.AreEqual(6, secondTraces.Data.Length);
        }

        [Test]
        public void Trace_filter_get_given_amount_of_traces()
        {
            Context context = new();
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.Count = 3;
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(3, traces.Data.Length);
        }

        [Test]
        public void Trace_filter_skip_and_get_the_rest_of_traces()
        {
            Context context = new();
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.Count = 3;
            traceFilterRequest.After = 7;
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(2, traces.Data.Length);
        }

        [Test]
        public void Trace_filter_with_filtering_by_receiver_address()
        {
            Context context = new();
            context.BuildNewBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce((UInt256)10 + 1).WithTo(TestItem.AddressA)
                        .SignedAndResolved().TestObject
                }
            );
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.FromBlock = new BlockParameter(10);
            traceFilterRequest.ToBlock = new BlockParameter(10);
            traceFilterRequest.ToAddress = new[] {TestItem.AddressA};
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(1, traces.Data.Length);
        }

        [Test]
        public void Trace_filter_with_filtering_by_sender()
        {
            Context context = new();
            context.BuildNewBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce((UInt256)10 + 1)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }
            );
            context.BuildNewBlock(new Transaction [0]);
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.FromBlock = new BlockParameter(10);
            traceFilterRequest.ToBlock = BlockParameter.Latest;
            traceFilterRequest.FromAddress = new[] {TestItem.PrivateKeyA.Address};
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(1, traces.Data.Length);
        }

        [Test]
        public void Trace_filter_with_filtering_by_sender_and_receiver()
        {
            Context context = new();
            context.BuildNewBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce((UInt256)10 + 3).WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                    Build.A.Transaction.WithNonce((UInt256)10 + 2).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyB).TestObject,
                    Build.A.Transaction.WithNonce((UInt256)10 + 1).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }
            );
            context.BuildNewBlock(new Transaction [0]);
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.FromBlock = new BlockParameter(10);
            traceFilterRequest.ToBlock = BlockParameter.Latest;
            traceFilterRequest.FromAddress = new[] {TestItem.PrivateKeyA.Address};
            traceFilterRequest.ToAddress = new[] {TestItem.AddressC};
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(1, traces.Data.Length);
        }

        [Test]
        public void Trace_filter_complex_scenario()
        {
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.After = 3;
            traceFilterRequest.Count = 4;
            traceFilterRequest.FromBlock = new BlockParameter(10);
            traceFilterRequest.ToBlock = BlockParameter.Latest;
            traceFilterRequest.FromAddress = new[] {TestItem.PrivateKeyA.Address, TestItem.PrivateKeyD.Address};
            traceFilterRequest.ToAddress = new[] {TestItem.AddressC, TestItem.AddressA, TestItem.AddressB};
            Context context = new();

            // first block skipped: After 3 -> 1 
            context.BuildNewBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce((UInt256)10 + 1).WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // skipped
                    Build.A.Transaction.WithNonce((UInt256)10 + 2).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --After
                    Build.A.Transaction.WithNonce((UInt256)10 + 3).WithTo(TestItem.AddressB) // --After
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }
            );

            // second block: After 1 -> 0, Count 4 -> 3
            context.BuildNewBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce((UInt256)10 + 4).WithTo(TestItem.AddressA)
                        .SignedAndResolved(TestItem.PrivateKeyC).TestObject, // skipped
                    Build.A.Transaction.WithNonce((UInt256)10 + 5).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // --After
                    Build.A.Transaction.WithNonce((UInt256)10 + 6).WithTo(TestItem.AddressB)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // --Count
                    Build.A.Transaction.WithNonce((UInt256)10 + 7)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject // skipped
                }
            );

            // third block: Count 3 -> 1
            context.BuildNewBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce((UInt256)10 + 7).WithTo(TestItem.AddressB)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --Count
                    Build.A.Transaction.WithNonce((UInt256)10 + 7).WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // skipped
                    Build.A.Transaction.WithNonce((UInt256)10 + 8).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject // skipped
                }
            );
            // fourth block: Count 1 -> 0
            context.BuildNewBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce((UInt256)20).WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // skipped
                    Build.A.Transaction.WithNonce((UInt256)21).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --Count
                    Build.A.Transaction.WithNonce((UInt256)22).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject // skipped (Count == 0)
                }
            );

            // the last block: skipped
            context.BuildNewBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce((UInt256)10 + 2).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                    Build.A.Transaction.WithNonce((UInt256)10 + 3).WithTo(TestItem.AddressB)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }
            );

            context.BuildNewBlock(new Transaction [0]);

            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(traceFilterRequest.Count, traces.Data.Length);
        }

        [Test]
        public async Task trace_timeout_is_separate_for_rpc_calls()
        {
            Context context = new();
            IJsonRpcConfig jsonRpcConfig = context.JsonRpcConfig;
            jsonRpcConfig.Timeout = 25;
            ITraceRpcModule traceRpcModule = context.TraceRpcModule;

            BlockParameter searchParameter = new(number: 0);
            Assert.DoesNotThrow(() => traceRpcModule.trace_block(searchParameter));

            await Task.Delay(jsonRpcConfig.Timeout +
                             25); //additional second just to show that in this time span timeout should occur if given one for whole class

            Assert.DoesNotThrow(() => traceRpcModule.trace_block(searchParameter));
        }
    }
}
