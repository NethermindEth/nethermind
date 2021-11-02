using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MathNet.Numerics.Optimization.TrustRegion;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using NUnit.Framework;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class TraceRpcModuleTests
    {
        private class Context
        {
            public async Task Build(ISpecProvider specProvider = null)
            {
                JsonRpcConfig = new JsonRpcConfig();
                Blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(specProvider);
                await Blockchain.AddFunds(TestItem.AddressA, 1000.Ether());
                await Blockchain.AddFunds(TestItem.AddressB, 1000.Ether());
                await Blockchain.AddFunds(TestItem.AddressC, 1000.Ether());
                ReadOnlyDbProvider? dbProvider = Blockchain.DbProvider.AsReadOnly(false);
                ReceiptsRecovery receiptsRecovery =
                    new (Blockchain.EthereumEcdsa, Blockchain.SpecProvider);
                IReceiptFinder receiptFinder = new FullInfoReceiptFinder(Blockchain.ReceiptStorage, receiptsRecovery, Blockchain.BlockFinder);
                ReadOnlyTxProcessingEnv txProcessingEnv =
                    new(dbProvider, Blockchain.ReadOnlyTrieStore, Blockchain.BlockTree.AsReadOnly(), Blockchain.SpecProvider, Blockchain.LogManager);
                RewardCalculator? rewardCalculatorSource = new RewardCalculator(Blockchain.SpecProvider);
            
                IRewardCalculator rewardCalculator = rewardCalculatorSource.Get(txProcessingEnv.TransactionProcessor);
            
                ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                    txProcessingEnv,
                    Always.Valid,
                    Blockchain.BlockPreprocessorStep,
                    rewardCalculator,
                    Blockchain.ReceiptStorage,
                    dbProvider,
                    Blockchain.SpecProvider,
                    Blockchain.LogManager);
                TraceRpcModule = new TraceRpcModule(receiptFinder, new Tracer(chainProcessingEnv.StateProvider, chainProcessingEnv.ChainProcessor),
                    Blockchain.BlockFinder, JsonRpcConfig, MainnetSpecProvider.Instance, LimboLogs.Instance);
                
                for (int i = 1; i < 10; i++)
                {
                    List<Transaction> transactions = new();
                    for (int j = 0; j < i; j++)
                    {
                        transactions.Add(Core.Test.Builders.Build.A.Transaction.WithNonce(Blockchain.State.GetAccount(TestItem.AddressB).Nonce + (UInt256)j).SignedAndResolved(Blockchain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject);
                    }
                    await Blockchain.AddBlock(transactions.ToArray());
                }
            }
            public ITraceRpcModule TraceRpcModule { get; private set;  }
            public IJsonRpcConfig JsonRpcConfig { get; private set; }
            
            public TestRpcBlockchain Blockchain { get; set; }
            
        }
        [Test]
        public async Task Tx_positions_are_fine()
        {
            Context context = new();
            await context.Build();
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_block", "latest");
                // , serialized.Replace("\"", "\\\"")
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xa1e0e640b433d5a8931881b8eee7b1a125474b04e430c0bf8afff52584c53273\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x5cf5d4a0a93000beb1cfb373508ce4c0153ab491be99b3c927f482346c86a0e1\",\"transactionPosition\":1,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x02d2cde9120e37722f607771ebaa0d4e98c5d99a8a9e7df6872e8c8c9f5c0bc5\",\"transactionPosition\":2,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xe50a2a2d170011b1f9ee080c3810bed0c63dbb1b2b2c541c78ada5b222cc3fd2\",\"transactionPosition\":3,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xff0d4524d379fc15c41a9b0444b943e1a530779b7d09c8863858267c5ef92b24\",\"transactionPosition\":4,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xf9b69366c82084e3799dc4a7ad87dc173ef4923d853bc250de86b81786f2972a\",\"transactionPosition\":5,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x28171c29b23cd96f032fe43f444402af4555ee5f074d5d0d0a1089d940f136e7\",\"transactionPosition\":6,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x09b01caf4b7ecfe9d02251b2e478f2da0fdf08412e3fa1ff963fa80635dab031\",\"transactionPosition\":7,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xd82382905afbe4ca4c2b8e54cea43818c91e0014c3827e3020fbd82b732b8239\",\"transactionPosition\":8,\"type\":\"call\"},{\"action\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"rewardType\":\"block\",\"value\":\"0x1bc16d674ec80000\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":null,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"id\":67}", serialized);
        }

        [Test]
        public async Task Trace_filter_return_fail_with_not_existing_block()
        {
            Context context = new();
            await context.Build();
            string request = "{\"fromBlock\":\"0x154\",\"after\":0}";
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_filter", request);
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Block 340 could not be found\"},\"id\":67}",
                serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Trace_filter_return_invalid_params()
        {
            Context context = new();
            await context.Build();
            string request =
                "[[{\"from\":\"0xfe35e70599578efef562e1f1cdc9ef693b865e9d\",\"to\":\"0x8cf85548ae57a91f8132d0831634c0fcef06e505\"},[\"trace\"]],[{\"from\":\"0x2a6ae6f33729384a00b4ffbd25e3f1bf1b9f5b8d\",\"to\":\"0xab736519b5433974059da38da74b8db5376942cd\",\"gasPrice\":\"0xb2b29a6dc\"},[\"trace\"]]]";

            string request2 = "\"latest\"";

            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_callMany", request,request2);

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xfe35e70599578efef562e1f1cdc9ef693b865e9d\",\"gas\":\"0x2fa9e78\",\"input\":\"0x\",\"to\":\"0x8cf85548ae57a91f8132d0831634c0fcef06e505\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x2a6ae6f33729384a00b4ffbd25e3f1bf1b9f5b8d\",\"gas\":\"0x2fa9e78\",\"input\":\"0x\",\"to\":\"0xab736519b5433974059da38da74b8db5376942cd\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null}],\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Trace_filter_return_fail_from_block_higher_than_to_block()
        {
            Context context = new();
            await context.Build();
            string request = "{\"fromBlock\":\"0x8\",\"toBlock\":\"0x6\"}";
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_filter", request);
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"From block number: 8 is greater than to block number 6\"},\"id\":67}",
                serialized, serialized.Replace("\"", "\\\""));
        }
        
        [Test]
        public async Task Trace_filter_return_empty_result_with_count_0()
        {
            Context context = new();
            await context.Build();
            string request = "{\"count\":0x0, \"fromBlock\":\"0x3\",\"toBlock\":\"0x3\"}";
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_filter", request);
            Assert.AreEqual(
                "{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}",
                serialized, serialized.Replace("\"", "\\\""));
        }


        [Test]
        public async Task Trace_filter_return_expected_json()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceFilterRequest = new();
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_filter", new EthereumJsonSerializer().Serialize(traceFilterRequest));

            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xa1e0e640b433d5a8931881b8eee7b1a125474b04e430c0bf8afff52584c53273\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x5cf5d4a0a93000beb1cfb373508ce4c0153ab491be99b3c927f482346c86a0e1\",\"transactionPosition\":1,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x02d2cde9120e37722f607771ebaa0d4e98c5d99a8a9e7df6872e8c8c9f5c0bc5\",\"transactionPosition\":2,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xe50a2a2d170011b1f9ee080c3810bed0c63dbb1b2b2c541c78ada5b222cc3fd2\",\"transactionPosition\":3,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xff0d4524d379fc15c41a9b0444b943e1a530779b7d09c8863858267c5ef92b24\",\"transactionPosition\":4,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xf9b69366c82084e3799dc4a7ad87dc173ef4923d853bc250de86b81786f2972a\",\"transactionPosition\":5,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x28171c29b23cd96f032fe43f444402af4555ee5f074d5d0d0a1089d940f136e7\",\"transactionPosition\":6,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x09b01caf4b7ecfe9d02251b2e478f2da0fdf08412e3fa1ff963fa80635dab031\",\"transactionPosition\":7,\"type\":\"call\"},{\"action\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"rewardType\":\"block\",\"value\":\"0x1bc16d674ec80000\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":null,\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xd82382905afbe4ca4c2b8e54cea43818c91e0014c3827e3020fbd82b732b8239\",\"transactionPosition\":8,\"type\":\"reward\"}],\"id\":67}", serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Trace_filter_skip_expected_number_of_traces()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceRequest = new();
            traceRequest.After = 3;
            ResultWrapper<ParityTxTraceFromStore[]> secondTraces = context.TraceRpcModule.trace_filter(traceRequest);
            Assert.AreEqual(6, secondTraces.Data.Length);
        }
        [Test]
        public async Task Trace_filter_get_given_amount_of_traces()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.Count = 3;
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(3, traces.Data.Length);
        }
        [Test]
        public async Task Trace_filter_skip_and_get_the_rest_of_traces()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.Count = 3;
            traceFilterRequest.After = 7;
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(2, traces.Data.Length);
        }
        [Test]
        public async Task Trace_filter_with_filtering_by_receiver_address()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA)
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(blockchain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;
            await context.Blockchain.AddBlock(transaction);
                
            TraceFilterForRpc traceFilterRequest = new();
            long lastBLockNumber = blockchain.BlockTree.Head!.Number;
            traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber);
            traceFilterRequest.ToBlock = new BlockParameter(lastBLockNumber);
            traceFilterRequest.ToAddress = new[] {TestItem.AddressA};
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(1, traces.Data.Length);
        }
        [Test]
        public async Task Trace_filter_with_filtering_by_sender()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA)
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(blockchain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;
            await context.Blockchain.AddBlock(transaction);
            await context.Blockchain.AddBlock();
            long lastBLockNumber = blockchain.BlockTree.Head!.Number;
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber - 1);
            traceFilterRequest.ToBlock = BlockParameter.Latest;
            traceFilterRequest.FromAddress = new[] {TestItem.PrivateKeyA.Address};
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(1, traces.Data.Length);
        }
        [Test]
        public async Task Trace_filter_with_filtering_by_sender_and_receiver()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            await context.Blockchain.AddBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce(currentNonceAddressA + 1).WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                    Build.A.Transaction.WithNonce(blockchain.State.GetAccount(TestItem.AddressB).Nonce + 1).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyB).TestObject,
                    Build.A.Transaction.WithNonce(currentNonceAddressA).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }
            );
            await context.Blockchain.AddBlock();
            TraceFilterForRpc traceFilterRequest = new();
            long lastBLockNumber = blockchain.BlockTree.Head!.Number;
            traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber - 1);
            traceFilterRequest.ToBlock = BlockParameter.Latest;
            traceFilterRequest.FromAddress = new[] {TestItem.PrivateKeyA.Address};
            traceFilterRequest.ToAddress = new[] {TestItem.AddressC};
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(1, traces.Data.Length);
        }
        [Test]
        public async Task Trace_filter_complex_scenario()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceFilterRequest = new();
            TestRpcBlockchain blockchain = context.Blockchain;
            long lastBLockNumber = blockchain.BlockTree.Head!.Number;
            traceFilterRequest.After = 3;
            traceFilterRequest.Count = 4;
            traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber + 1);
            traceFilterRequest.ToBlock = BlockParameter.Latest;
            traceFilterRequest.FromAddress = new[] {TestItem.PrivateKeyA.Address, TestItem.PrivateKeyD.Address};
            traceFilterRequest.ToAddress = new[] {TestItem.AddressC, TestItem.AddressA, TestItem.AddressB};
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            UInt256 currentNonceAddressC = blockchain.State.GetAccount(TestItem.AddressC).Nonce;
            UInt256 currentNonceAddressD = blockchain.State.GetAccount(TestItem.AddressD).Nonce;
            // first block skipped: After 3 -> 1 
            await blockchain.AddBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // skipped
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --After
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressB) // --After
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }
            );
            // second block: After 1 -> 0, Count 4 -> 3
            await blockchain.AddBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce(currentNonceAddressC++).WithTo(TestItem.AddressA)
                        .SignedAndResolved(TestItem.PrivateKeyC).TestObject, // skipped
                    Build.A.Transaction.WithNonce(currentNonceAddressD++).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // --After
                    Build.A.Transaction.WithNonce(currentNonceAddressD++).WithTo(TestItem.AddressB)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // --Count
                    Build.A.Transaction.WithNonce(currentNonceAddressD++)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject // skipped
                }
            );
            // third block: Count 3 -> 1
            await blockchain.AddBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressB)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --Count
                    Build.A.Transaction.WithNonce(currentNonceAddressD++).WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // skipped
                    Build.A.Transaction.WithNonce(currentNonceAddressD++).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyD).TestObject // skipped
                }
            );
            // fourth block: Count 1 -> 0
            await blockchain.AddBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressD)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // skipped
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --Count
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject // skipped (Count == 0)
                }
            );
            // the last block: skipped
            await blockchain.AddBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                    Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressB)
                        .SignedAndResolved(TestItem.PrivateKeyA).TestObject
                }
            );
            await blockchain.AddBlock();
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.AreEqual(traceFilterRequest.Count, traces.Data.Length);
        }
        [Test]
        public async Task trace_transaction_and_get_simple_tx()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            await blockchain.AddBlock(transaction);
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_transaction(transaction.Hash!);
            Assert.AreEqual(1, traces.Data.Length);
            Assert.AreEqual(transaction.Hash!, traces.Data[0].TransactionHash);
            
            long[] positions = {0};
            ResultWrapper<ParityTxTraceFromStore[]> traceGet = context.TraceRpcModule.trace_get(transaction.Hash!, positions);
            Assert.AreEqual(0, traceGet.Data.Length);
        }

        [Test]
        public async Task trace_get_can_trace_simple_tx()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;

            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            await blockchain.AddBlock(transaction);

            long[] positions = {0};
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_get(transaction.Hash!, positions);
            Assert.AreEqual(0, traces.Data.Length);
        }

        [Test]
        public async Task trace_transaction_and_get_internal_tx()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            UInt256 currentNonceAddressB = blockchain.State.GetAccount(TestItem.AddressB).Nonce;
            await blockchain.AddFunds(TestItem.AddressA, 10000.Ether());
            byte[] deployedCode = new byte[3];
            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;
            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;
            
            Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
                .WithData(createCode)
                .WithTo(null)
                .WithGasLimit(93548).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            await blockchain.AddBlock(transaction1);
            Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
            byte[] code = Prepare.EvmCode
                .Call(contractAddress, 50000)
                .Call(contractAddress, 50000)
                .Op(Instruction.STOP)
                .Done;
            
            Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressB++)
                .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
                .WithTo(null)
                .WithGasLimit(93548).TestObject;
            await blockchain.AddBlock(transaction2);

            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_transaction(transaction2.Hash!);
            Assert.AreEqual(3, traces.Data.Length);
            // Assert.NotNull(traces.Data[0].Error);
            Assert.AreEqual(transaction2.Hash!, traces.Data[0].TransactionHash);

            long[] positions = {0};
            ResultWrapper<ParityTxTraceFromStore[]> tracesGet = context.TraceRpcModule.trace_get(transaction2.Hash!, positions);
            Assert.AreEqual(1, tracesGet.Data.Length);
            Assert.AreEqual(transaction2.Hash!, tracesGet.Data[0].TransactionHash);
            Assert.AreEqual(traces.Data[1].TransactionHash, tracesGet.Data[0].TransactionHash);
            Assert.AreEqual(traces.Data[1].BlockHash, tracesGet.Data[0].BlockHash);
            Assert.AreEqual(traces.Data[1].BlockNumber, tracesGet.Data[0].BlockNumber);
        }
        
        [Test]
        public async Task trace_transaction_with_error_reverted()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            UInt256 currentNonceAddressB = blockchain.State.GetAccount(TestItem.AddressB).Nonce;
            await blockchain.AddFunds(TestItem.AddressA, 10000.Ether());
            byte[] deployedCode = new byte[3];
            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;
            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;
            
            Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
                .WithData(createCode)
                .WithTo(null)
                .WithGasLimit(93548).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            await blockchain.AddBlock(transaction1);
            Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
            byte[] code = Prepare.EvmCode
                .Call(contractAddress, 50000)
                .Call(contractAddress, 50000)
                .Op(Instruction.REVERT)
                .Done;
            
            Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressB++)
                .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
                .WithTo(null)
                .WithGasLimit(93548).TestObject;
            await blockchain.AddBlock(transaction2);
            ResultWrapper<ParityTxTraceFromStore[]> traces = context.TraceRpcModule.trace_transaction(transaction2.Hash!);
            Assert.AreEqual(3, traces.Data.Length);
            Assert.AreEqual(transaction2.Hash!, traces.Data[0].TransactionHash);
            string serialized = new EthereumJsonSerializer().Serialize(traces.Data);
          
            Assert.AreEqual("[{\"action\":{\"traceAddress\":[],\"callType\":\"create\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"create\",\"creationMethod\":\"create\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"gas\":\"0x9a6c\",\"value\":\"0x1\",\"input\":\"0x60006000600060006000736b5887043de753ecfa6269f947129068263ffbe261c350f160006000600060006000736b5887043de753ecfa6269f947129068263ffbe261c350f1fd\",\"subtraces\":[{\"traceAddress\":[0],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"creationMethod\":\"create2\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x2dcd\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]},{\"traceAddress\":[1],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"creationMethod\":\"create2\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x2d56\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]}],\"error\":\"Reverted\"},\"blockHash\":\"0xfa74e932520ee416ecb12171c115b3ad14112ffd2d612646ceaff69e54e06a94\",\"blockNumber\":18,\"result\":null,\"subtraces\":2,\"traceAddress\":[],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"create\",\"error\":\"Reverted\"},{\"action\":{\"traceAddress\":[0],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"creationMethod\":\"create2\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x2dcd\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]},\"blockHash\":\"0xfa74e932520ee416ecb12171c115b3ad14112ffd2d612646ceaff69e54e06a94\",\"blockNumber\":18,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[0],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"traceAddress\":[1],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"creationMethod\":\"create2\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x2d56\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]},\"blockHash\":\"0xfa74e932520ee416ecb12171c115b3ad14112ffd2d612646ceaff69e54e06a94\",\"blockNumber\":18,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[1],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"call\"}]",serialized, serialized.Replace("\"", "\\\""));
        }
        [Test]
        public async Task trace_timeout_is_separate_for_rpc_calls()
        {
            Context context = new();
            await context.Build();
            IJsonRpcConfig jsonRpcConfig = context.JsonRpcConfig;
            jsonRpcConfig.Timeout = 25;
            ITraceRpcModule traceRpcModule = context.TraceRpcModule;
            BlockParameter searchParameter = new(number: 0);
            Assert.DoesNotThrow(() => traceRpcModule.trace_block(searchParameter));
            await Task.Delay(jsonRpcConfig.Timeout +
                             25); //additional second just to show that in this time span timeout should occur if given one for whole class
            Assert.DoesNotThrow(() => traceRpcModule.trace_block(searchParameter));
        }
        [Test]
        public async Task trace_replayTransaction_test()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            UInt256 currentNonceAddressB = blockchain.State.GetAccount(TestItem.AddressB).Nonce;
            await blockchain.AddFunds(TestItem.AddressA, 10000.Ether());
            byte[] deployedCode = new byte[3];
            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;
            
            Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
            byte[] code = Prepare.EvmCode
                .Call(contractAddress, 50000)
                .Call(contractAddress, 50000)
                .Op(Instruction.STOP)
                .Done;
            
            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressB++)
                .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
                .WithTo(TestItem.AddressC)
                .WithGasLimit(93548).TestObject;
            await blockchain.AddBlock(transaction);
            string[] traceTypes = {"trace"};
            ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_replayTransaction(transaction.Hash!, traceTypes);
            Assert.AreEqual(TestItem.AddressB, traces.Data.Action.From);
            Assert.AreEqual(TestItem.AddressC, traces.Data.Action.To);
            Assert.AreEqual("call", traces.Data.Action.CallType);
            Assert.IsTrue(traces.GetResult().ResultType == ResultType.Success);
        }
        
        [Test]
        public async Task trace_replayTransaction_reward_test()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            UInt256 currentNonceAddressB = blockchain.State.GetAccount(TestItem.AddressB).Nonce;
            await blockchain.AddFunds(TestItem.AddressA, 10000.Ether());
            Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
            byte[] code = Prepare.EvmCode
                .Call(contractAddress, 50000)
                .Call(contractAddress, 50000)
                .Op(Instruction.STOP)
                .Done;
            
            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressB++)
                .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
                .WithTo(TestItem.AddressC)
                .WithGasLimit(93548).TestObject;
            await blockchain.AddBlock(transaction);
            string[] traceTypes = {"rewards"};
            ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_replayTransaction(transaction.Hash!, traceTypes);
            Assert.AreEqual("reward", traces.Data.Action.CallType);
            Assert.AreEqual(UInt256.Parse("2000000000000000000"), traces.Data.Action.Value);
            Assert.IsTrue(traces.GetResult().ResultType == ResultType.Success);
        }

        [Test]
        public async Task trace_call_test()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            UInt256 currentNonceAddressB = blockchain.State.GetAccount(TestItem.AddressB).Nonce;
            await blockchain.AddFunds(TestItem.AddressA, 10000.Ether());

            Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
            byte[] code = Prepare.EvmCode
                .Call(contractAddress, 50000)
                .Call(contractAddress, 50000)
                .Op(Instruction.STOP)
                .Done;

            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressB++)
                .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
                .WithTo(TestItem.AddressC)
                .WithGasLimit(93548).TestObject;
            await blockchain.AddBlock(transaction);


            Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressB++)
                .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
                .WithTo(TestItem.AddressC)
                .WithGasLimit(93548).TestObject;
            await blockchain.AddBlock(transaction2);

            TransactionForRpc transactionRpc = new(transaction2);

            string[] traceTypes = {"trace"};

            ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_call(transactionRpc, traceTypes,null);
            Assert.AreEqual("call", traces.Data.Action.CallType);
            Assert.AreEqual(TestItem.AddressB, traces.Data.Action.From);
            Assert.AreEqual(TestItem.AddressC, traces.Data.Action.To);
        }

        public async Task trace_call_test2()
        {
            Context context = new();
            await context.Build();

            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            await blockchain.AddBlock(transaction);
            TransactionForRpc txForRpc = new(transaction);
            string[] traceTypes = {"Trace"};

            ResultWrapper<ParityTxTraceFromReplay> tr = context.TraceRpcModule.trace_call(txForRpc, traceTypes, new BlockParameter(16));
            Assert.AreEqual(TestItem.AddressC, tr.Data.Action.To);
            Assert.AreEqual("call", tr.Data.Action.Type.ToString());
        }

        [TestCase("{\"from\":\"0xaaaaaaaa8583de65cc752fe3fad5098643244d22\",\"to\":\"0xd6a8d04cb9846759416457e2c593c99390092df6\"}", "[\"trace\"]","{\"jsonrpc\":\"2.0\",\"result\":{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xaaaaaaaa8583de65cc752fe3fad5098643244d22\",\"gas\":\"0x2fa9e78\",\"input\":\"0x\",\"to\":\"0xd6a8d04cb9846759416457e2c593c99390092df6\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},\"id\":67}")]
        [TestCase("{\"from\":\"0x7f554713be84160fdf0178cc8df86f5aabd33397\",\"to\":\"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f\",\"value\":\"0x0\",\"gasPrice\":\"0x119e04a40a\"}", "[\"trace\"]","{\"jsonrpc\":\"2.0\",\"result\":{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x7f554713be84160fdf0178cc8df86f5aabd33397\",\"gas\":\"0x2fa9e78\",\"input\":\"0x\",\"to\":\"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},\"id\":67}")]
        [TestCase("{\"from\":\"0xc71acc7863f3bc7347b24c3b835643bd89d4d161\",\"to\":\"0xa760e26aa76747020171fcf8bda108dfde8eb930\",\"value\":\"0x0\",\"gasPrice\":\"0x2108eea5bc\"}", "[\"trace\"]", "{\"jsonrpc\":\"2.0\",\"result\":{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xc71acc7863f3bc7347b24c3b835643bd89d4d161\",\"gas\":\"0x2fa9e78\",\"input\":\"0x\",\"to\":\"0xa760e26aa76747020171fcf8bda108dfde8eb930\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},\"id\":67}")]
        public async Task Trace_call_without_blockParameter_test(string request1, string request2, string expectedResult)
        {
            Context context = new();
            await context.Build();
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_call", request1, request2);

            Assert.AreEqual(expectedResult, serialized, serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task trace_callMany_test()
        {
            Context context = new();
            await context.Build();

            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;

            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            TransactionForRpc txForRpc = new(transaction);
            string[] traceTypes = {"Trace"};

            Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressD)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            await blockchain.AddBlock(transaction, transaction2);

            TransactionForRpc txForRpc2 = new(transaction2);
            string[] traceTypes2 = {"Trace"};

            BlockParameter numberOrTag = new BlockParameter(16);

            Tuple<TransactionForRpc, string[]>[] a = {new(txForRpc, traceTypes), new(txForRpc2, traceTypes2)};

            ResultWrapper<ParityTxTraceFromReplay[]> tr = context.TraceRpcModule.trace_callMany(a, numberOrTag);
            Assert.AreEqual(2, tr.Data.Length);

        }


    }
}
