// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
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
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class TraceRpcModuleTests
    {
        private class Context
        {
            public async Task Build(ISpecProvider? specProvider = null, bool isAura = false)
            {
                JsonRpcConfig = new JsonRpcConfig();
                Blockchain = await TestRpcBlockchain.ForTest(isAura ? SealEngineType.AuRa : SealEngineType.NethDev).Build(specProvider);
                await Blockchain.AddFunds(TestItem.AddressA, 1000.Ether());
                await Blockchain.AddFunds(TestItem.AddressB, 1000.Ether());
                await Blockchain.AddFunds(TestItem.AddressC, 1000.Ether());
                ReadOnlyDbProvider? dbProvider = Blockchain.DbProvider.AsReadOnly(false);
                ReceiptsRecovery receiptsRecovery =
                    new(Blockchain.EthereumEcdsa, Blockchain.SpecProvider);
                IReceiptFinder receiptFinder = new FullInfoReceiptFinder(Blockchain.ReceiptStorage, receiptsRecovery, Blockchain.BlockFinder);
                ReadOnlyTxProcessingEnv txProcessingEnv =
                    new(dbProvider, Blockchain.ReadOnlyTrieStore, Blockchain.BlockTree.AsReadOnly(), Blockchain.SpecProvider, Blockchain.LogManager);
                RewardCalculator rewardCalculatorSource = new(Blockchain.SpecProvider);

                IRewardCalculator rewardCalculator = rewardCalculatorSource.Get(txProcessingEnv.TransactionProcessor);

                RpcBlockTransactionsExecutor rpcBlockTransactionsExecutor = new(txProcessingEnv.TransactionProcessor, txProcessingEnv.StateProvider);
                ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                    txProcessingEnv,
                    Always.Valid,
                    Blockchain.BlockPreprocessorStep,
                    rewardCalculator,
                    Blockchain.ReceiptStorage,
                    dbProvider,
                    Blockchain.SpecProvider,
                    Blockchain.LogManager,
                    rpcBlockTransactionsExecutor);

                Tracer tracer = new(chainProcessingEnv.StateProvider, chainProcessingEnv.ChainProcessor);
                TraceRpcModule = new TraceRpcModule(receiptFinder, tracer, Blockchain.BlockFinder, JsonRpcConfig, MainnetSpecProvider.Instance, LimboLogs.Instance);

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

            public ITraceRpcModule TraceRpcModule { get; private set; } = null!;
            public IJsonRpcConfig JsonRpcConfig { get; private set; } = null!;
            public TestRpcBlockchain Blockchain { get; set; } = null!;

        }
        [Test]
        public async Task Tx_positions_are_fine()
        {
            Context context = new();
            await context.Build();
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_block", "latest");
            Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xa1e0e640b433d5a8931881b8eee7b1a125474b04e430c0bf8afff52584c53273\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x5cf5d4a0a93000beb1cfb373508ce4c0153ab491be99b3c927f482346c86a0e1\",\"transactionPosition\":1,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x02d2cde9120e37722f607771ebaa0d4e98c5d99a8a9e7df6872e8c8c9f5c0bc5\",\"transactionPosition\":2,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xe50a2a2d170011b1f9ee080c3810bed0c63dbb1b2b2c541c78ada5b222cc3fd2\",\"transactionPosition\":3,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xff0d4524d379fc15c41a9b0444b943e1a530779b7d09c8863858267c5ef92b24\",\"transactionPosition\":4,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xf9b69366c82084e3799dc4a7ad87dc173ef4923d853bc250de86b81786f2972a\",\"transactionPosition\":5,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x28171c29b23cd96f032fe43f444402af4555ee5f074d5d0d0a1089d940f136e7\",\"transactionPosition\":6,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x09b01caf4b7ecfe9d02251b2e478f2da0fdf08412e3fa1ff963fa80635dab031\",\"transactionPosition\":7,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xd82382905afbe4ca4c2b8e54cea43818c91e0014c3827e3020fbd82b732b8239\",\"transactionPosition\":8,\"type\":\"call\"},{\"action\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"rewardType\":\"block\",\"value\":\"0x1bc16d674ec80000\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"id\":67}"));
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
            Assert.That(
                serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Block 340 could not be found\"},\"id\":67}"), serialized.Replace("\"", "\\\""));
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
            Assert.That(
                serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"From block number: 8 is greater than to block number 6\"},\"id\":67}"), serialized.Replace("\"", "\\\""));
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
            Assert.That(
                serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"), serialized.Replace("\"", "\\\""));
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

            Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xa1e0e640b433d5a8931881b8eee7b1a125474b04e430c0bf8afff52584c53273\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x5cf5d4a0a93000beb1cfb373508ce4c0153ab491be99b3c927f482346c86a0e1\",\"transactionPosition\":1,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x02d2cde9120e37722f607771ebaa0d4e98c5d99a8a9e7df6872e8c8c9f5c0bc5\",\"transactionPosition\":2,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xe50a2a2d170011b1f9ee080c3810bed0c63dbb1b2b2c541c78ada5b222cc3fd2\",\"transactionPosition\":3,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xff0d4524d379fc15c41a9b0444b943e1a530779b7d09c8863858267c5ef92b24\",\"transactionPosition\":4,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xf9b69366c82084e3799dc4a7ad87dc173ef4923d853bc250de86b81786f2972a\",\"transactionPosition\":5,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x28171c29b23cd96f032fe43f444402af4555ee5f074d5d0d0a1089d940f136e7\",\"transactionPosition\":6,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x09b01caf4b7ecfe9d02251b2e478f2da0fdf08412e3fa1ff963fa80635dab031\",\"transactionPosition\":7,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xd82382905afbe4ca4c2b8e54cea43818c91e0014c3827e3020fbd82b732b8239\",\"transactionPosition\":8,\"type\":\"call\"},{\"action\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"rewardType\":\"block\",\"value\":\"0x1bc16d674ec80000\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"id\":67}"), serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Trace_filter_skip_expected_number_of_traces()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceRequest = new();
            traceRequest.After = 3;
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> secondTraces = context.TraceRpcModule.trace_filter(traceRequest);
            Assert.That(secondTraces.Data.Count(), Is.EqualTo(7));
        }
        [Test]
        public async Task Trace_filter_get_given_amount_of_traces()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.Count = 3;
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.That(traces.Data.Count(), Is.EqualTo(3));
        }
        [Test]
        public async Task Trace_filter_skip_and_get_the_rest_of_traces()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.Count = 3;
            traceFilterRequest.After = 7;
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            // Total 9 transactions in block + 1 reward trace - after skipping 7 - it should be 3
            Assert.That(traces.Data.Count(), Is.EqualTo(3));
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
            traceFilterRequest.ToAddress = new[] { TestItem.AddressA };
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.That(traces.Data.Count(), Is.EqualTo(1));
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
            traceFilterRequest.FromAddress = new[] { TestItem.PrivateKeyA.Address };
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.That(traces.Data.Count(), Is.EqualTo(1));
        }

        [Test]
        public async Task Trace_filter_with_filtering_by_internal_transaction_receiver()
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
            await blockchain.AddBlock();
            long lastBLockNumber = blockchain.BlockTree.Head!.Number;

            TraceFilterForRpc traceFilterRequest = new();
            traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber - 1);
            traceFilterRequest.ToBlock = BlockParameter.Latest;
            traceFilterRequest.ToAddress = new[] { contractAddress };
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.That(traces.Data.Count(), Is.EqualTo(2));

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
            traceFilterRequest.FromAddress = new[] { TestItem.PrivateKeyA.Address };
            traceFilterRequest.ToAddress = new[] { TestItem.AddressC };
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.That(traces.Data.Count(), Is.EqualTo(1));
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
            traceFilterRequest.FromAddress = new[] { TestItem.PrivateKeyA.Address, TestItem.PrivateKeyD.Address };
            traceFilterRequest.ToAddress = new[] { TestItem.AddressC, TestItem.AddressA, TestItem.AddressB };
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
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.That(traces.Data.Count(), Is.EqualTo(traceFilterRequest.Count));
        }

        [Test]
        public async Task Trace_filter_complex_scenario_openethereum()
        {
            Context context = new();
            await context.Build();
            TraceFilterForRpc traceFilterRequest = new();
            TestRpcBlockchain blockchain = context.Blockchain;
            long lastBLockNumber = blockchain.BlockTree.Head!.Number;
            // traceFilterRequest.After = 3;
            // traceFilterRequest.Count = 4;
            traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber + 1);
            traceFilterRequest.ToBlock = BlockParameter.Latest;
            // traceFilterRequest.FromAddress = new[] {TestItem.PrivateKeyA.Address, TestItem.PrivateKeyD.Address};
            // traceFilterRequest.ToAddress = new[] {TestItem.AddressC, TestItem.AddressA, TestItem.AddressB};
            UInt256 currentNonceAddressC = blockchain.State.GetAccount(TestItem.AddressC).Nonce;
            await blockchain.AddBlock();
            await blockchain.AddBlock();
            await blockchain.AddBlock(
                new[]
                {
                    Build.A.Transaction.WithNonce(currentNonceAddressC++).WithTo(TestItem.AddressA)
                        .SignedAndResolved(TestItem.PrivateKeyC).TestObject,
                }
            );
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
            Assert.That(traces.Data.Count(), Is.EqualTo(4));
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
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_transaction(transaction.Hash!);
            traces.Data.Should().BeEquivalentTo(new[] { new { TransactionHash = transaction.Hash } }, o => o.Including(o => o.TransactionHash));

            long[] positions = { 0 };
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traceGet = context.TraceRpcModule.trace_get(transaction.Hash!, positions);
            traceGet.Data.Should().BeEmpty();
        }

        [Test]
        public async Task Trace_get_can_trace_simple_tx()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;

            Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            await blockchain.AddBlock(transaction);

            long[] positions = { 0 };
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_get(transaction.Hash!, positions);
            traces.Data.Should().BeEmpty();
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

            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_transaction(transaction2.Hash!);
            traces.Data.Should().HaveCount(3);
            traces.Data.ElementAt(0).TransactionHash.Should().Be(transaction2.Hash!);

            long[] positions = { 0 };
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> tracesGet = context.TraceRpcModule.trace_get(transaction2.Hash!, positions);
            traces.Data.ElementAt(0).TransactionHash.Should().BeEquivalentTo(transaction2.Hash);
            traces.Data.ElementAt(1).Should().BeEquivalentTo(tracesGet.Data.ElementAt(0));
        }

        [Test]
        public async Task Trace_transaction_with_error_reverted()
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
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_transaction(transaction2.Hash!);
            traces.Data.Should().HaveCount(3);
            traces.Data.ElementAt(0).TransactionHash.Should().Be(transaction2.Hash!);
            string serialized = new EthereumJsonSerializer().Serialize(traces.Data);

            Assert.That(serialized, Is.EqualTo("[{\"action\":{\"traceAddress\":[],\"callType\":\"create\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"create\",\"creationMethod\":\"create\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"gas\":\"0x9a6c\",\"value\":\"0x1\",\"input\":\"0x60006000600060006000736b5887043de753ecfa6269f947129068263ffbe261c350f160006000600060006000736b5887043de753ecfa6269f947129068263ffbe261c350f1fd\",\"subtraces\":[{\"traceAddress\":[0],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x2dcd\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]},{\"traceAddress\":[1],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x2d56\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]}],\"error\":\"Reverted\"},\"blockHash\":\"0xfa74e932520ee416ecb12171c115b3ad14112ffd2d612646ceaff69e54e06a94\",\"blockNumber\":18,\"subtraces\":2,\"traceAddress\":[],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"create\",\"error\":\"Reverted\"},{\"action\":{\"traceAddress\":[0],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x2dcd\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]},\"blockHash\":\"0xfa74e932520ee416ecb12171c115b3ad14112ffd2d612646ceaff69e54e06a94\",\"blockNumber\":18,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[0],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"traceAddress\":[1],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x2d56\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]},\"blockHash\":\"0xfa74e932520ee416ecb12171c115b3ad14112ffd2d612646ceaff69e54e06a94\",\"blockNumber\":18,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[1],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"call\"}]"), serialized.Replace("\"", "\\\""));
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
        public async Task Trace_replayTransaction_test()
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
            string[] traceTypes = { "trace" };
            ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_replayTransaction(transaction.Hash!, traceTypes);
            Assert.That(traces.Data.Action!.From, Is.EqualTo(TestItem.AddressB));
            Assert.That(traces.Data.Action.To, Is.EqualTo(TestItem.AddressC));
            Assert.That(traces.Data.Action.CallType, Is.EqualTo("call"));
            Assert.IsTrue(traces.GetResult().ResultType == ResultType.Success);
        }

        [Test]
        public async Task Trace_replayTransaction_reward_test()
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
            string[] traceTypes = { "rewards" };
            ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_replayTransaction(transaction.Hash!, traceTypes);
            Assert.That(traces.Data.Action!.CallType, Is.EqualTo("reward"));
            Assert.That(traces.Data.Action.Value, Is.EqualTo(UInt256.Parse("2000000000000000000")));
            Assert.IsTrue(traces.GetResult().ResultType == ResultType.Success);
        }

        [Test]
        public async Task trace_replayBlockTransactions_zeroGasUsed_test()
        {
            Context context = new();
            OverridableReleaseSpec releaseSpec = new(London.Instance);
            releaseSpec.Eip1559TransitionBlock = 1;
            TestSpecProvider specProvider = new(releaseSpec);
            await context.Build(specProvider, isAura: true);
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressC = blockchain.State.GetAccount(TestItem.AddressC).Nonce;

            Transaction serviceTransaction = Build.A.Transaction.WithNonce(currentNonceAddressC++)
                .WithTo(TestItem.AddressE)
                .SignedAndResolved(TestItem.PrivateKeyC)
                .WithIsServiceTransaction(true).TestObject;
            await blockchain.AddBlock(serviceTransaction);
            BlockParameter blockParameter = new BlockParameter(BlockParameterType.Latest);
            string[] traceTypes = { "trace" };
            ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> traces = context.TraceRpcModule.trace_replayBlockTransactions(blockParameter, traceTypes);
            traces.Data.First().Action!.Result!.GasUsed.Should().Be(0);
        }

        [Test]
        public async Task Trace_call_without_blockParameter_provided_test()
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

            string[] traceTypes = { "trace" };

            ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_call(transactionRpc, traceTypes);
            Assert.That(traces.Data.Action!.CallType, Is.EqualTo("call"));
            Assert.That(traces.Data.Action.From, Is.EqualTo(TestItem.AddressB));
            Assert.That(traces.Data.Action.To, Is.EqualTo(TestItem.AddressC));
        }

        [Test]
        public async Task Trace_call_simple_tx_test()
        {
            Context context = new();
            await context.Build();
            string transaction = "{\"from\":\"0xaaaaaaaa8583de65cc752fe3fad5098643244d22\",\"to\":\"0xd6a8d04cb9846759416457e2c593c99390092df6\"}";
            string traceTypes = "[\"trace\"]";
            string blockParameter = "latest";
            string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xaaaaaaaa8583de65cc752fe3fad5098643244d22\",\"gas\":\"0x5f58ef8\",\"input\":\"0x\",\"to\":\"0xd6a8d04cb9846759416457e2c593c99390092df6\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},\"id\":67}";

            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_call", transaction, traceTypes, blockParameter);

            Assert.That(serialized, Is.EqualTo(expectedResult), serialized.Replace("\"", "\\\""));
        }

        [TestCase("{\"from\":\"0x7f554713be84160fdf0178cc8df86f5aabd33397\",\"to\":\"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f\",\"value\":\"0x0\",\"gasPrice\":\"0x119e04a40a\"}", "[\"trace\"]", "{\"jsonrpc\":\"2.0\",\"result\":{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x7f554713be84160fdf0178cc8df86f5aabd33397\",\"gas\":\"0x5f58ef8\",\"input\":\"0x\",\"to\":\"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},\"id\":67}")]
        [TestCase("{\"from\":\"0xc71acc7863f3bc7347b24c3b835643bd89d4d161\",\"to\":\"0xa760e26aa76747020171fcf8bda108dfde8eb930\",\"value\":\"0x0\",\"gasPrice\":\"0x2108eea5bc\"}", "[\"trace\"]", "{\"jsonrpc\":\"2.0\",\"result\":{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xc71acc7863f3bc7347b24c3b835643bd89d4d161\",\"gas\":\"0x5f58ef8\",\"input\":\"0x\",\"to\":\"0xa760e26aa76747020171fcf8bda108dfde8eb930\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},\"id\":67}")]
        public async Task Trace_call_without_blockParameter_test(string transaction, string traceTypes, string expectedResult)
        {
            Context context = new();
            await context.Build();
            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_call", transaction, traceTypes);

            Assert.That(serialized, Is.EqualTo(expectedResult), serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Trace_callMany_internal_transactions_test()
        {
            Context context = new();
            await context.Build();

            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;

            Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            TransactionForRpc txForRpc1 = new(transaction1);
            string[] traceTypes1 = { "Trace" };

            Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressD)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            await blockchain.AddBlock(transaction1, transaction2);

            TransactionForRpc txForRpc2 = new(transaction2);
            string[] traceTypes2 = { "Trace" };

            BlockParameter numberOrTag = new(16);
            TransactionForRpcWithTraceTypes tr1 = new();
            TransactionForRpcWithTraceTypes tr2 = new();
            tr1.Transaction = txForRpc1;
            tr1.TraceTypes = traceTypes1;
            tr2.Transaction = txForRpc2;
            tr2.TraceTypes = traceTypes2;

            TransactionForRpcWithTraceTypes[] a = { tr1, tr2 };

            ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> tr = context.TraceRpcModule.trace_callMany(a, numberOrTag);
            tr.Data.Should().HaveCount(2);
        }

        [Test]
        public async Task Trace_callMany_is_blockParameter_optional_test()
        {
            Context context = new();
            await context.Build();
            string calls = "[[{\"from\":\"0xfe35e70599578efef562e1f1cdc9ef693b865e9d\",\"to\":\"0x8cf85548ae57a91f8132d0831634c0fcef06e505\"},[\"trace\"]],[{\"from\":\"0x2a6ae6f33729384a00b4ffbd25e3f1bf1b9f5b8d\",\"to\":\"0xab736519b5433974059da38da74b8db5376942cd\",\"gasPrice\":\"0xb2b29a6dc\"},[\"trace\"]]]";

            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_callMany", calls, "latest");

            string serialized_without_blockParameter_param = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_callMany", calls);

            Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xfe35e70599578efef562e1f1cdc9ef693b865e9d\",\"gas\":\"0x5f58ef8\",\"input\":\"0x\",\"to\":\"0x8cf85548ae57a91f8132d0831634c0fcef06e505\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x2a6ae6f33729384a00b4ffbd25e3f1bf1b9f5b8d\",\"gas\":\"0x5f58ef8\",\"input\":\"0x\",\"to\":\"0xab736519b5433974059da38da74b8db5376942cd\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null}],\"id\":67}"), serialized.Replace("\"", "\\\""));
            Assert.That(serialized_without_blockParameter_param, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xfe35e70599578efef562e1f1cdc9ef693b865e9d\",\"gas\":\"0x5f58ef8\",\"input\":\"0x\",\"to\":\"0x8cf85548ae57a91f8132d0831634c0fcef06e505\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x2a6ae6f33729384a00b4ffbd25e3f1bf1b9f5b8d\",\"gas\":\"0x5f58ef8\",\"input\":\"0x\",\"to\":\"0xab736519b5433974059da38da74b8db5376942cd\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null}],\"id\":67}"), serialized_without_blockParameter_param.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Trace_callMany_accumulates_state_changes()
        {
            Context context = new();
            await context.Build();

            string calls = $"[[{{\"from\":\"{TestItem.AddressA}\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"1\"}},[\"statediff\"]],[{{\"from\":\"{TestItem.AddressA}\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"1\"}},[\"statediff\"]]]";

            string serialized = RpcTest.TestSerializedRequest(
                EthModuleFactory.Converters.Union(TraceModuleFactory.Converters).ToList(), context.TraceRpcModule,
                "trace_callMany", calls);

            Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"output\":null,\"stateDiff\":{\"0x0000000000000000000000000000000000000000\":{\"balance\":{\"*\":{\"from\":\"0x24\",\"to\":\"0x25\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}},\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x3635c9adc5de9f09e5\",\"to\":\"0x3635c9adc5de9f09e4\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x3\",\"to\":\"0x4\"}},\"storage\":{}}},\"trace\":[],\"vmTrace\":null},{\"output\":null,\"stateDiff\":{\"0x0000000000000000000000000000000000000000\":{\"balance\":{\"*\":{\"from\":\"0x25\",\"to\":\"0x26\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}},\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x3635c9adc5de9f09e4\",\"to\":\"0x3635c9adc5de9f09e3\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x4\",\"to\":\"0x5\"}},\"storage\":{}}},\"trace\":[],\"vmTrace\":null}],\"id\":67}"), serialized.Replace("\"", "\\\""));
        }

        [Test]
        public async Task Trace_replayBlockTransactions_transactions_deploying_contract()
        {
            Context context = new();
            await context.Build();
            TestRpcBlockchain blockchain = context.Blockchain;
            UInt256 currentNonceAddressA = blockchain.State.GetAccount(TestItem.AddressA).Nonce;
            await blockchain.AddFunds(TestItem.AddressA, 10000.Ether());
            Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);

            byte[] code = Prepare.EvmCode
                .Call(contractAddress, 50000)
                .Done;

            Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
                .WithSenderAddress(TestItem.AddressA)
                .WithData(code)
                .WithTo(null)
                .WithGasLimit(93548).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
                .WithSenderAddress(TestItem.AddressA)
                .WithData(code).SignedAndResolved(TestItem.PrivateKeyA)
                .WithTo(null)
                .WithGasLimit(93548).TestObject;

            string[] traceTypes = { "trace" };

            await blockchain.AddBlock(transaction1, transaction2);

            ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> traces = context.TraceRpcModule.trace_replayBlockTransactions(new BlockParameter(blockchain.BlockFinder.FindLatestBlock()!.Number), traceTypes);
            traces.Data.Should().HaveCount(2);
            traces.Data.ElementAt(0).Action!.From.Should().BeEquivalentTo(traces.Data.ElementAt(1).Action!.From);
            string serialized = new EthereumJsonSerializer().Serialize(traces.Data);
            Assert.That(serialized, Is.EqualTo("[{\"output\":\"0x\",\"transactionHash\":\"0x8513c9083ec27fa8e3ca7e3ffa732d61562e2d17e2e1af6e773bc810dc4c3452\",\"action\":{\"traceAddress\":[],\"callType\":\"create\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"create\",\"creationMethod\":\"create\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"gas\":\"0x9c70\",\"value\":\"0x1\",\"input\":\"0x60006000600060006000730ffd3e46594919c04bcfd4e146203c825567082861c350f1\",\"result\":{\"gasUsed\":\"0x79\",\"output\":\"0x\",\"address\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"code\":\"0x\"},\"subtraces\":[{\"traceAddress\":[0],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"from\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"to\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"gas\":\"0x9988\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]}]}},{\"output\":\"0x\",\"transactionHash\":\"0xa6a56c7927deae778a749bcdab7bbf409c0d8a5d2420021a3ba328240ae832d8\",\"action\":{\"traceAddress\":[],\"callType\":\"create\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"create\",\"creationMethod\":\"create\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x9c70\",\"value\":\"0x1\",\"input\":\"0x60006000600060006000730ffd3e46594919c04bcfd4e146203c825567082861c350f1\",\"result\":{\"gasUsed\":\"0xa3d\",\"output\":\"0x\",\"address\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"code\":\"0x\"},\"subtraces\":[{\"traceAddress\":[0],\"callType\":\"call\",\"includeInTrace\":true,\"isPrecompiled\":false,\"type\":\"call\",\"from\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"to\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"gas\":\"0x8feb\",\"value\":\"0x0\",\"input\":\"0x\",\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":[]}]}}]"));
        }
    }
}
