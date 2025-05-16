// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Json;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.Stats.Model;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using Nethermind.Specs;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Network.Rlpx;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SubscribeModuleTests
    {
        private ISubscribeRpcModule _subscribeRpcModule = null!;
        private ILogManager _logManager = null!;
        private IBlockTree _blockTree = null!;
        private ITxPool _txPool = null!;
        private IReceiptStorage _receiptStorage = null!;
        private IFilterStore _filterStore = null!;
        private ISubscriptionManager _subscriptionManager = null!;
        private IJsonRpcDuplexClient _jsonRpcDuplexClient = null!;
        private IJsonSerializer _jsonSerializer = null!;
        private ISpecProvider _specProvider = null!;
        private IReceiptMonitor _receiptCanonicalityMonitor = null!;
        private ISyncConfig _syncConfig = null!;
        private ISyncProgressResolver _syncProgressResolver = null!;
        private EthSyncingInfo _ethSyncingInfo;
        private IPeerPool _peerPool;
        private IRlpxHost _rlpxPeer;

        [SetUp]
        public void Setup()
        {
            _logManager = Substitute.For<ILogManager>();
            _blockTree = Substitute.For<IBlockTree>();
            _txPool = Substitute.For<ITxPool>();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _specProvider = Substitute.For<ISpecProvider>();
            _filterStore = new FilterStore(new TimerFactory());
            _jsonRpcDuplexClient = Substitute.For<IJsonRpcDuplexClient>();
            _jsonSerializer = new EthereumJsonSerializer();
            _receiptCanonicalityMonitor = new ReceiptCanonicalityMonitor(_receiptStorage, _logManager);
            _syncConfig = new SyncConfig();
            _syncProgressResolver = Substitute.For<ISyncProgressResolver>();
            _ethSyncingInfo = new EthSyncingInfo(_blockTree, Substitute.For<ISyncPointers>(), _syncConfig,
                new StaticSelector(SyncMode.All), _syncProgressResolver, _logManager);
            _peerPool = Substitute.For<IPeerPool>();
            _rlpxPeer = Substitute.For<IRlpxHost>();

            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();

            SubscriptionFactory subscriptionFactory = new();

            // Register the standard subscription types in the dictionary
            subscriptionFactory.RegisterStandardEthSubscriptions(_blockTree, _logManager, _specProvider, _receiptCanonicalityMonitor, _filterStore, _txPool, _ethSyncingInfo);

            _subscriptionManager = new SubscriptionManager(
            subscriptionFactory,
            _logManager);

            _subscribeRpcModule = new SubscribeRpcModule(_subscriptionManager);
            _subscribeRpcModule.Context = new JsonRpcContext(RpcEndpoint.Ws, _jsonRpcDuplexClient);

            // block numbers matching filters in LogsSubscriptions with null arguments will be 33333-77777
            BlockHeader fromBlock = Build.A.BlockHeader.WithNumber(33333).TestObject;
            BlockHeader toBlock = Build.A.BlockHeader.WithNumber(77777).TestObject;
            _blockTree.FindHeader(Arg.Any<BlockParameter>()).Returns(fromBlock);
            _blockTree.FindHeader(Arg.Any<BlockParameter>(), true).Returns(toBlock);

            _specProvider.ChainId.Returns((ulong)BlockchainIds.Mainnet);
        }

        [TearDown]
        public void TearDown()
        {
            _jsonRpcDuplexClient?.Dispose();
            _receiptCanonicalityMonitor?.Dispose();
            _filterStore.Dispose();
        }

        private JsonRpcResult GetBlockAddedToMainResult(BlockReplacementEventArgs blockReplacementEventArgs, out string subscriptionId, TransactionsOption? options = null, bool shouldReceiveResult = true)
        {
            NewHeadSubscription newHeadSubscription = new(_jsonRpcDuplexClient, _blockTree, _logManager, _specProvider, options);

            JsonRpcResult jsonRpcResult = new();

            ManualResetEvent manualResetEvent = new(false);
            newHeadSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockReplacementEventArgs);
            _receiptStorage.ReceiptsInserted += Raise.EventWith(new object(), blockReplacementEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);

            subscriptionId = newHeadSubscription.Id;
            return jsonRpcResult;
        }

        private List<JsonRpcResult> GetLogsSubscriptionResult(Filter filter, BlockReplacementEventArgs blockEventArgs, out string subscriptionId)
        {
            LogsSubscription logsSubscription = new(_jsonRpcDuplexClient, _receiptCanonicalityMonitor, _filterStore, _blockTree, _logManager, filter);

            List<JsonRpcResult> jsonRpcResults = new();

            SemaphoreSlim semaphoreSlim = new(0, 1);
            logsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResults.Add(j);
            }));

            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockEventArgs);
            _receiptStorage.ReceiptsInserted += Raise.EventWith(new object(), blockEventArgs);
            semaphoreSlim.Wait(TimeSpan.FromMilliseconds(500));

            subscriptionId = logsSubscription.Id;
            return jsonRpcResults;
        }

        private JsonRpcResult GetNewPendingTransactionsResult(TxEventArgs txEventArgs, out string subscriptionId, TransactionsOption? option = null)
        {
            NewPendingTransactionsSubscription newPendingTransactionsSubscription = new(_jsonRpcDuplexClient, _txPool, _specProvider, _logManager, option);
            JsonRpcResult jsonRpcResult = new();

            ManualResetEvent manualResetEvent = new(false);
            newPendingTransactionsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            _txPool.NewPending += Raise.EventWith(new object(), txEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));

            subscriptionId = newPendingTransactionsSubscription.Id;
            return jsonRpcResult;
        }

        private JsonRpcResult GetDroppedPendingTransactionsResult(TxEventArgs txEventArgs, out string subscriptionId)
        {
            DroppedPendingTransactionsSubscription droppedPendingTransactionsSubscription = new(_jsonRpcDuplexClient, _txPool, _logManager);
            JsonRpcResult jsonRpcResult = new();

            ManualResetEvent manualResetEvent = new(false);
            droppedPendingTransactionsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            _txPool.EvictedPending += Raise.EventWith(new object(), txEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000));

            subscriptionId = droppedPendingTransactionsSubscription.Id;
            return jsonRpcResult;
        }

        private SyncingSubscription GetSyncingSubscription(int bestSuggested, int head)
        {
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(bestSuggested).TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);

            Block block = Build.A.Block.WithNumber(head).TestObject;
            _blockTree.Head.Returns(block);

            EthSyncingInfo ethSyncingInfo = new(_blockTree, Substitute.For<ISyncPointers>(), _syncConfig,
                new StaticSelector(SyncMode.All), _syncProgressResolver, _logManager);

            SyncingSubscription syncingSubscription = new(_jsonRpcDuplexClient, _blockTree, ethSyncingInfo, _logManager);

            return syncingSubscription;
        }

        private JsonRpcResult GetSyncingSubscriptionResult(bool newHead, SyncingSubscription syncingSubscription, BlockEventArgs blockEventArgs)
        {
            JsonRpcResult jsonRpcResult = new();

            ManualResetEvent manualResetEvent = new(false);
            syncingSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            if (newHead) _blockTree.NewHeadBlock += Raise.EventWith(new object(), blockEventArgs);
            else _blockTree.NewBestSuggestedBlock += Raise.EventWith(new object(), blockEventArgs);

            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));

            return jsonRpcResult;
        }

        private JsonRpcResult GetPeerEventsAddResult(PeerEventArgs peerEventArgs, out string subscriptionId, bool shouldReceiveResult = true)
        {
            PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
            JsonRpcResult jsonRpcResult = new();
            ManualResetEvent manualResetEvent = new(false);
            peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));
            _peerPool.PeerAdded += Raise.EventWith(new object(), peerEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);
            subscriptionId = peerEventsSubscription.Id;
            return jsonRpcResult;
        }
        private JsonRpcResult GetPeerEventsRemovedResult(PeerEventArgs peerEventArgs, out string subscriptionId, bool shouldReceiveResult = true)
        {
            PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
            JsonRpcResult jsonRpcResult = new();
            ManualResetEvent manualResetEvent = new(false);
            peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));
            _peerPool.PeerRemoved += Raise.EventWith(new object(), peerEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);
            subscriptionId = peerEventsSubscription.Id;
            return jsonRpcResult;
        }

        [Test]
        public async Task Wrong_subscription_name()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "wrongSubscriptionType");
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Wrong subscription type: wrongSubscriptionType.\"},\"id\":67}";
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task No_subscription_name()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe");
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\",\"data\":\"Incorrect parameters count, expected: 2, actual: 0\"},\"id\":67}";
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task NewHeadSubscription_creating_result()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newHeads");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task NewHeadSubscription_with_includeTransactions_arg()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newHeads", "{\"includeTransactions\":true}");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [TestCase("true")]
        [TestCase("false")]
        public async Task NewHeadSubscription_with_bool_arg(string boolArg)
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newHeads", boolArg);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewHeadSubscription_on_BlockAddedToMain_event2()
        {
            Block block = Build.A.Block.WithDifficulty(1991).WithExtraData(new byte[] { 3, 5, 8 }).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block);
            TransactionsOption option = new()
            {
                IncludeTransactions = true
            };

            JsonRpcResult jsonRpcResult = GetBlockAddedToMainResult(blockReplacementEventArgs, out string subscriptionId, option);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"difficulty\":\"0x7c7\",\"extraData\":\"0x030508\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2e3c1c2a507dc3071a16300858d4e75390e5f43561515481719a1e0dadf22585\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x200\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewHeadSubscription_on_BlockAddedToMain_event()
        {
            Block block = Build.A.Block.WithDifficulty(1991).WithExtraData(new byte[] { 3, 5, 8 }).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block);

            JsonRpcResult jsonRpcResult = GetBlockAddedToMainResult(blockReplacementEventArgs, out string subscriptionId);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"difficulty\":\"0x7c7\",\"extraData\":\"0x030508\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2e3c1c2a507dc3071a16300858d4e75390e5f43561515481719a1e0dadf22585\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x200\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewHeadSubscription_on_BlockAddedToMain_event_with_null_block()
        {
            BlockReplacementEventArgs blockReplacementEventArgs = new(null!);

            JsonRpcResult jsonRpcResult = GetBlockAddedToMainResult(blockReplacementEventArgs, out _, shouldReceiveResult: false);

            jsonRpcResult.Response.Should().BeNull();
        }

        [Test]
        public void NewHeadSubscription_should_send_notifications_when_adding_multiple_blocks_at_once_and_after_reorgs()
        {
            MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;
            BlockTree blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithSpecProvider(specProvider)
                .TestObject;

            NewHeadSubscription newHeadSubscription = new(_jsonRpcDuplexClient, blockTree, _logManager, specProvider);
            ConcurrentQueue<JsonRpcResult> jsonRpcResult = new();

            Block block0 = Build.A.Block.Genesis.WithTotalDifficulty(0L).TestObject;
            Block block1 = Build.A.Block.WithParent(block0).WithDifficulty(1).WithTotalDifficulty(1L).TestObject;
            Block block2 = Build.A.Block.WithParent(block1).WithDifficulty(2).WithTotalDifficulty(3L).TestObject;
            Block block3 = Build.A.Block.WithParent(block2).WithDifficulty(3).WithTotalDifficulty(6L).TestObject;
            Block block1B = Build.A.Block.WithParent(block0).WithDifficulty(4).WithTotalDifficulty(4L).TestObject;
            Block block2B = Build.A.Block.WithParent(block1B).WithDifficulty(5).WithTotalDifficulty(9L).TestObject;

            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            blockTree.SuggestBlock(block2);
            blockTree.SuggestBlock(block3);
            blockTree.SuggestBlock(block1B);
            blockTree.SuggestBlock(block2B);

            ManualResetEvent manualResetEvent = new(false);
            newHeadSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult.Enqueue(j);

                if (jsonRpcResult.Count is 3 or 5)
                    manualResetEvent.Set();
            }));

            blockTree.UpdateMainChain(new Block[] { block1, block2, block3 }, true);
            manualResetEvent.WaitOne();
            manualResetEvent.Reset();
            blockTree.UpdateMainChain(new Block[] { block1B, block2B }, true);
            manualResetEvent.WaitOne();

            jsonRpcResult.Count.Should().Be(5);
            blockTree.Head.Should().Be(block2B);

            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Last().Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", newHeadSubscription.Id, "\",\"result\":{\"difficulty\":\"0x5\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x13f51c304a84742a660b0327c003765af51cb255f7cfa8d1d6c41c99c1c3ecd4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x2\",\"parentHash\":\"0xd07062cc54724bd878b1b826bfa59f24cac986a11a151f2239b16f2a4436f9b2\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x1fe\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x9\",\"timestamp\":\"0xf4242\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}}}");
            serialized.Should().Be(expectedResult);
        }

        [Test]
        public void NewHeadSubscription_should_send_notifications_in_order()
        {
            MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;

            BlockTree blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithSpecProvider(specProvider)
                .TestObject;

            NewHeadSubscription newHeadSubscription = new(_jsonRpcDuplexClient, blockTree, _logManager, specProvider);
            ConcurrentQueue<JsonRpcResult> jsonRpcResult = new();

            Block block0 = Build.A.Block.Genesis.WithDifficulty(0).WithTotalDifficulty(0L).TestObject;

            List<Block> blocks = new() { block0 };

            for (int i = 1; i < 21; i++)
            {
                var difficulty = (UInt256)i;
                blocks.Add(Build.A.Block.WithParent(blocks[i - 1]).WithDifficulty(difficulty).WithTotalDifficulty(blocks[i - 1].TotalDifficulty + difficulty).TestObject);
            }

            foreach (Block block in blocks)
            {
                blockTree.SuggestBlock(block);
            }

            ManualResetEvent manualResetEvent = new(false);
            newHeadSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult.Enqueue(j);

                if (jsonRpcResult.Count == 21)
                {
                    manualResetEvent.Set();
                }
            }));

            blockTree.UpdateMainChain(blocks, true);

            manualResetEvent.WaitOne();

            jsonRpcResult.Count.Should().Be(21);
            blockTree.Head.Should().Be(blocks[20]);

            for (int i = 0; i < 21; i++)
            {
                jsonRpcResult.TryDequeue(out var result);

                ((BlockForRpc)((JsonRpcSubscriptionResponse)result.Response!).Params.Result).Difficulty.Should().Be((UInt256)i);
            }
        }

        [Test]
        public async Task LogsSubscription_with_null_arguments_creating_result()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "logs");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task LogsSubscription_with_valid_arguments_creating_result()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "logs", "{\"fromBlock\":\"latest\",\"toBlock\":\"latest\",\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"topics\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"}");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task LogsSubscription_dispose_regression()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "logs", "{\"address\":[\"0x0000000000000000000000000000000000000314\"],\"fromBlock\":\"0x0\",\"toBlock\":\"latest\",\"topics\":[]}");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task LogsSubscription_with_invalid_arguments_creating_result()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "logs", "trambabamba");
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\"},\"id\":67}";
            expectedResult.Should().Be(serialized);
        }

        [Test]
        [Retry(3)]
        public void LogsSubscription_with_null_arguments_on_NewHeadBlock_event()
        {
            int blockNumber = 55555;
            Filter filter = Substitute.For<Filter>();

            LogEntry logEntry = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            TxReceipt[] txReceipts = { Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntry).TestObject };
            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);

            Block block = Build.A.Block.WithNumber(blockNumber).TestObject;
            BlockReplacementEventArgs blockEventArgs = new(block);

            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, blockEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(1);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void LogsSubscription_with_not_matching_block_on_NewHeadBlock_event()
        {
            int blockNumber = 22222;
            Filter filter = Substitute.For<Filter>();

            LogEntry logEntry = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            TxReceipt[] txReceipts = { Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntry).TestObject };
            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);

            Block block = Build.A.Block.WithNumber(blockNumber).TestObject;
            BlockReplacementEventArgs blockEventArgs = new(block);

            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, blockEventArgs, out string _);

            jsonRpcResults.Count.Should().Be(0);
        }

        [Test]
        public void LogsSubscription_with_null_arguments_on_NewHeadBlock_event_with_one_TxReceipt_with_few_logs()
        {
            int blockNumber = 77777;
            Filter filter = Substitute.For<Filter>();

            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithData(TestItem.RandomDataC).TestObject;

            TxReceipt[] txReceipts = { Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryB, logEntryC).TestObject };
            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);

            Block block = Build.A.Block.WithNumber(blockNumber).TestObject;
            BlockReplacementEventArgs blockEventArgs = new(block);

            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, blockEventArgs, out string subscriptionId);

            jsonRpcResults.Count.Should().Be(3);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0x12fd1\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"blockNumber\":\"0x12fd1\",\"data\":\"0x\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x0000000000000000000000000000000000000000\",\"blockNumber\":\"0x12fd1\",\"data\":\"0x010208090a\",\"logIndex\":\"0x2\",\"removed\":false,\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void LogsSubscription_with_null_arguments_on_NewHeadBlock_event_with_few_TxReceipts_with_few_logs()
        {
            int blockNumber = 55555;
            Filter filter = Substitute.For<Filter>();

            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithData(TestItem.RandomDataC).TestObject;

            TxReceipt[] txReceipts =
            {
            Build.A.Receipt.WithBlockNumber(blockNumber).WithIndex(11).WithLogs(logEntryA).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithIndex(22).WithLogs(logEntryA, logEntryB).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithIndex(33).WithLogs(logEntryB, logEntryC).TestObject
        };

            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);

            Block block = Build.A.Block.WithNumber(blockNumber).TestObject;
            BlockReplacementEventArgs blockEventArgs = new(block);

            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, blockEventArgs, out string subscriptionId);

            jsonRpcResults.Count.Should().Be(5);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0xb\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x16\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"blockNumber\":\"0xd903\",\"data\":\"0x\",\"logIndex\":\"0x2\",\"removed\":false,\"topics\":[\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\"],\"transactionIndex\":\"0x16\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[3].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"blockNumber\":\"0xd903\",\"data\":\"0x\",\"logIndex\":\"0x3\",\"removed\":false,\"topics\":[\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\"],\"transactionIndex\":\"0x21\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[4].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x0000000000000000000000000000000000000000\",\"blockNumber\":\"0xd903\",\"data\":\"0x010208090a\",\"logIndex\":\"0x4\",\"removed\":false,\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"],\"transactionIndex\":\"0x21\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void LogsSubscription_on_NewHeadBlock_event_with_few_TxReceipts_with_few_logs_with_some_address_mismatches()
        {
            int blockNumber = 55555;
            Filter filter = new()
            {
                FromBlock = BlockParameter.Latest,
                ToBlock = BlockParameter.Latest,
                Address = "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                Topics = new[] { TestItem.KeccakA }
            };

            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).WithData(TestItem.RandomDataB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithData(TestItem.RandomDataC).TestObject;

            TxReceipt[] txReceipts =
            {
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryB, logEntryC).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs().TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryB).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryC, logEntryC, logEntryB, logEntryC, logEntryC, logEntryB).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryC, logEntryB, logEntryA, logEntryC).TestObject,
        };

            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);

            Block block = Build.A.Block.WithNumber(blockNumber).WithBloom(new Bloom(txReceipts.Select(r => r.Bloom).ToArray())).TestObject;
            BlockReplacementEventArgs blockEventArgs = new(block);

            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, blockEventArgs, out var subscriptionId);

            Assert.That(() => jsonRpcResults.Count, Is.EqualTo(3).After(1000, 100));

            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0xa\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0xd\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void LogsSubscription_on_NewHeadBlock_event_with_few_TxReceipts_with_few_logs_with_some_topic_mismatches()
        {
            int blockNumber = 55555;

            Filter filter = new()
            {
                FromBlock = BlockParameter.Latest,
                ToBlock = BlockParameter.Latest,
                Address = "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                Topics = new[] { TestItem.KeccakA }
            };

            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).WithData(TestItem.RandomDataB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithAddress(TestItem.AddressC).WithData(TestItem.RandomDataC).TestObject;

            TxReceipt[] txReceipts =
            {
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryB, logEntryC).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs().TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryB).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryC, logEntryC, logEntryB, logEntryC, logEntryC, logEntryB).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryC, logEntryB, logEntryA, logEntryC).TestObject,
        };

            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);

            Block block = Build.A.Block.WithNumber(blockNumber).WithBloom(new Bloom(txReceipts.Select(static r => r.Bloom).ToArray())).TestObject;
            BlockReplacementEventArgs blockEventArgs = new(block);

            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, blockEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(3);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0xa\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0xd\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void LogsSubscription_on_NewHeadBlock_event_with_few_TxReceipts_with_few_logs_with_few_topics_and_some_address_and_topic_mismatches()
        {
            int blockNumber = 55555;
            IEnumerable<object> topics = new List<object> { TestItem.KeccakA };

            Filter filter = new()
            {
                FromBlock = BlockParameter.Latest,
                ToBlock = BlockParameter.Latest,
                Address = new[] { "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358" },
                Topics = new[] { TestItem.KeccakA, TestItem.KeccakD }
            };

            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakD).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressC).WithTopics(TestItem.KeccakA, TestItem.KeccakD).WithData(TestItem.RandomDataB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithData(TestItem.RandomDataC).TestObject;
            LogEntry logEntryD = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakA, TestItem.KeccakD, TestItem.KeccakE).WithData(TestItem.RandomDataB).TestObject;
            LogEntry logEntryE = Build.A.LogEntry.WithTopics(TestItem.KeccakA, TestItem.KeccakD).WithData(TestItem.RandomDataB).TestObject;

            TxReceipt[] txReceipts =
            {
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryB, logEntryC).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs().TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryB).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryE, logEntryE, logEntryB, logEntryD, logEntryE, logEntryB).TestObject,
            Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryC, logEntryB, logEntryE, logEntryA, logEntryB).TestObject,
        };

            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);

            Block block = Build.A.Block.WithNumber(blockNumber).WithBloom(new Bloom(txReceipts.Select(static r => r.Bloom).ToArray())).TestObject;
            BlockReplacementEventArgs blockEventArgs = new(block);

            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, blockEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(3);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"blockNumber\":\"0xd903\",\"data\":\"0x04050607\",\"logIndex\":\"0x7\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\",\"0x434b529473163ef4ed9c9341d9b7250ab9183c27e7add004c3bba38c56274e24\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);

            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0xd\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void LogsSubscription_should_not_send_logs_of_new_txs_on_ReceiptsInserted_event_but_on_NewHeadBlock_event()
        {
            int blockNumber = 55555;
            Filter filter = Substitute.For<Filter>();

            LogsSubscription logsSubscription = new(_jsonRpcDuplexClient, _receiptCanonicalityMonitor, _filterStore, _blockTree, _logManager, filter);

            LogEntry logEntry = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            TxReceipt[] txReceipts = { Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntry).TestObject };
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
            Block block = Build.A.Block.WithHeader(blockHeader).TestObject;
            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);

            List<JsonRpcResult> jsonRpcResults = new();

            ManualResetEvent manualResetEvent = new(false);
            ReceiptsEventArgs receiptsEventArgs = new(blockHeader, txReceipts, false);
            logsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResults.Add(j);
                manualResetEvent.Set();
            }));
            _receiptCanonicalityMonitor.ReceiptsInserted += Raise.EventWith(new object(), receiptsEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(200));

            jsonRpcResults.Count.Should().Be(0);

            BlockReplacementEventArgs blockEventArgs = new(block);
            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockEventArgs);
            _receiptStorage.ReceiptsInserted += Raise.EventWith(new object(), blockEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(200));

            jsonRpcResults.Count.Should().Be(1);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", logsSubscription.Id, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task NewPendingTransactionsSubscription_creating_result()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingTransactions");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task NewPendingTransactionsSubscription_creating_result_with_includeTransactions_arg()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingTransactions", "{\"includeTransactions\":true}");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [TestCase("true")]
        [TestCase("false")]
        public async Task NewPendingTransactionsSubscription_creating_result_with_bool_arg(string boolArg)
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingTransactions", boolArg);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewPendingTransactionsSubscription_on_NewPending_event()
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            transaction.Hash = TestItem.KeccakA;
            TxEventArgs txEventArgs = new(transaction);

            JsonRpcResult jsonRpcResult = GetNewPendingTransactionsResult(txEventArgs, out var subscriptionId);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":\"", TestItem.KeccakA, "\"}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewPendingTransactionsSubscription_on_NewPending_event_with_null_transaction()
        {
            TxEventArgs txEventArgs = new(null!);

            JsonRpcResult jsonRpcResult = GetNewPendingTransactionsResult(txEventArgs, out _);

            jsonRpcResult.Response.Should().BeNull();
        }

        [Test]
        public void NewPendingTransactionsSubscription_on_NewPending_event_with_null_transactions_hash()
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            transaction.Hash = null;
            TxEventArgs txEventArgs = new(transaction);

            JsonRpcResult jsonRpcResult = GetNewPendingTransactionsResult(txEventArgs, out var subscriptionId);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\"}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewPendingTransactionsSubscription_on_NewPending_with_includeTransactions_param()
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            transaction.Hash = null;
            TxEventArgs txEventArgs = new(transaction);

            TransactionsOption option = new()
            {
                IncludeTransactions = true
            };

            JsonRpcResult jsonRpcResult = GetNewPendingTransactionsResult(txEventArgs, out string subscriptionId, option);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);

            JToken.Parse(serialized).Should().BeEquivalentTo($$$$"""{"jsonrpc":"2.0","method":"eth_subscription","params":{"subscription":"{{{{subscriptionId}}}}","result":{"nonce":"0x0","blockHash":null,"blockNumber":null,"transactionIndex":null,"to":"0x0000000000000000000000000000000000000000","value":"0x1","gasPrice":"0x1","gas":"0x5208","input":"0x","type":"0x0","hash":null,"v":"0x0","r":"0x0","s":"0x0","from":null}}}""");
        }

        [TestCase(2)]
        [TestCase(5)]
        [TestCase(10)]
        [Explicit("Requires a WS server running")]
        public async Task NewPendingTransactionSubscription_multiple_fast_messages(int messages)
        {
            ITxPool txPool = Substitute.For<ITxPool>();

            using ClientWebSocket socket = new();
            await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

            using WebSocketMessageStream stream = new(socket, NullLogManager.Instance);
            using JsonRpcSocketsClient<WebSocketMessageStream> client = new(
                clientName: "TestClient",
                stream: stream,
                endpointType: RpcEndpoint.Ws,
                jsonRpcProcessor: null!,
                jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                jsonSerializer: new EthereumJsonSerializer()
            );

            using NewPendingTransactionsSubscription subscription = new(
                jsonRpcDuplexClient: client,
                txPool: txPool,
                specProvider: _specProvider,
                logManager: LimboLogs.Instance);

            for (int i = 0; i < messages; i++)
            {
                Transaction tx = new();
                txPool.NewPending += Raise.EventWith(new TxEventArgs(tx));
            }

            // Wait until all messages are sent
            await Task.Delay(1_000);
        }

        [TestCase(2)]
        [TestCase(5)]
        [TestCase(10)]
        [Explicit("Requires a WS server running")]
        public async Task MultipleSubscriptions_concurrent_fast_messages(int messages)
        {
            using ClientWebSocket socket = new();
            await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

            using WebSocketMessageStream stream = new(socket, NullLogManager.Instance);
            using JsonRpcSocketsClient<WebSocketMessageStream> client = new(
                clientName: "TestClient",
                stream: stream,
                endpointType: RpcEndpoint.Ws,
                jsonRpcProcessor: null!,
                jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                jsonSerializer: new EthereumJsonSerializer()
            );

            Task subA = Task.Run(() =>
            {
                ITxPool txPool = Substitute.For<ITxPool>();
                using NewPendingTransactionsSubscription subscription = new(
                    // ReSharper disable once AccessToDisposedClosure
                    jsonRpcDuplexClient: client,
                    txPool: txPool,
                    specProvider: _specProvider,
                    logManager: LimboLogs.Instance);

                for (int i = 0; i < messages; i++)
                {
                    Transaction tx = new();
                    txPool.NewPending += Raise.EventWith(new TxEventArgs(tx));
                }
            });
            Task subB = Task.Run(() =>
            {
                IBlockTree blockTree = Substitute.For<IBlockTree>();
                using NewHeadSubscription subscription = new(
                    // ReSharper disable once AccessToDisposedClosure
                    jsonRpcDuplexClient: client,
                    blockTree: blockTree,
                    specProvider: new TestSpecProvider(new ReleaseSpec()),
                    logManager: LimboLogs.Instance);

                for (int i = 0; i < messages; i++)
                {
                    BlockReplacementEventArgs eventArgs = new(Build.A.Block.TestObject);
                    blockTree.BlockAddedToMain += Raise.EventWith(eventArgs);
                    _receiptStorage.ReceiptsInserted += Raise.EventWith(new object(), eventArgs);
                }
            });

            await Task.WhenAll(subA, subB);

            // Wait until all messages are sent
            await Task.Delay(1_000);
        }

        [Test]
        public void NewHeadSubscription_with_baseFeePerGas_test()
        {
            _specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
            Block block = Build.A.Block.Genesis.WithTotalDifficulty(0L).WithBaseFeePerGas(10000).TestObject;
            BlockReplacementEventArgs blockReplacementEventArgs = new(block);
            JsonRpcResult jsonRpcResult = GetBlockAddedToMainResult(blockReplacementEventArgs, out string subscriptionId);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"" + subscriptionId + "\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x6f38eb4d3ad3beb1f9c6f870b32b55532f2f490bc33dc72696d6bb22dcef5d09\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x204\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x2710\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}}}";
            serialized.Should().Be(expectedResult);

        }

        [Test]
        public async Task DroppedPendingTransactionsSubscription_creating_result()
        {
            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "droppedPendingTransactions");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void DroppedPendingTransactionsSubscription_on_EvictedPending_event()
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            transaction.Hash = TestItem.KeccakA;
            TxEventArgs txEventArgs = new(transaction);

            JsonRpcResult jsonRpcResult = GetDroppedPendingTransactionsResult(txEventArgs, out var subscriptionId);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":\"", TestItem.KeccakA, "\"}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void DroppedPendingTransactionsSubscription_on_EvictedPending_event_with_null_transaction()
        {
            TxEventArgs txEventArgs = new(null!);

            JsonRpcResult jsonRpcResult = GetDroppedPendingTransactionsResult(txEventArgs, out _);

            jsonRpcResult.Response.Should().BeNull();
        }

        [Test]
        public void DroppedPendingTransactionsSubscription_on_EvictedPending_event_with_null_transactions_hash()
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            transaction.Hash = null;
            TxEventArgs txEventArgs = new(transaction);

            JsonRpcResult jsonRpcResult = GetDroppedPendingTransactionsResult(txEventArgs, out var subscriptionId);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\"}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task SyncingSubscription_creating_result()
        {
            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);

            Block block = Build.A.Block.TestObject;
            _blockTree.Head.Returns(block);

            string serialized = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "syncing");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void SyncingSubscription_on_NewHeadBlock_event_when_sync_no_change()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10024);

            Block blockChanged = Build.A.Block.WithNumber(10030).TestObject;
            BlockEventArgs blockEventArgs = new(blockChanged);
            _blockTree.Head.Returns(blockChanged);

            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(true, syncingSubscription, blockEventArgs);

            jsonRpcResult.Response.Should().BeNull();
        }

        [Test]
        public void SyncingSubscription_on_NewBestSuggestedBlock_event_when_sync_no_change()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10024);

            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(10045).TestObject;
            Block block = new(blockHeader);
            BlockEventArgs blockEventArgs = new(block);
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);

            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(false, syncingSubscription, blockEventArgs);

            jsonRpcResult.Response.Should().BeNull();
        }

        [Test]
        public void SyncingSubscription_on_NewHeadBlock_event_when_sync_changed_to_false()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10024);

            Block blockChanged = Build.A.Block.WithNumber(10040).TestObject;
            BlockEventArgs blockEventArgs = new(blockChanged);
            _blockTree.Head.Returns(blockChanged);

            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(true, syncingSubscription, blockEventArgs);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", syncingSubscription.Id, "\",\"result\":false}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void SyncingSubscription_on_NewBestSuggestedBlock_event_when_sync_changed_to_false()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10024);

            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(10030).TestObject;
            Block block = new(blockHeader);
            BlockEventArgs blockEventArgs = new(block);
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);

            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(false, syncingSubscription, blockEventArgs);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", syncingSubscription.Id, "\",\"result\":false}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void SyncingSubscription_on_NewHeadBlock_event_when_sync_changed_to_true()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10040);

            Block blockChanged = Build.A.Block.WithNumber(10024).TestObject;
            BlockEventArgs blockEventArgs = new(blockChanged);
            _blockTree.Head.Returns(blockChanged);

            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(true, syncingSubscription, blockEventArgs);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", syncingSubscription.Id, "\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x2728\",\"highestBlock\":\"0x273a\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void SyncingSubscription_on_NewBestSuggestedBlock_event_when_sync_changed_to_true()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10040);

            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(10099).TestObject;
            Block block = new(blockHeader);
            BlockEventArgs blockEventArgs = new(block);
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);

            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(true, syncingSubscription, blockEventArgs);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", syncingSubscription.Id, "\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x2738\",\"highestBlock\":\"0x2773\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public async Task Eth_unsubscribe_success()
        {
            string serializedSub = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newHeads");
            string subscriptionId = serializedSub.Substring(serializedSub.Length - 44, 34);
            string expectedSub = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", subscriptionId, "\",\"id\":67}");
            expectedSub.Should().Be(serializedSub);

            string serializedUnsub = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_unsubscribe", subscriptionId);
            string expectedUnsub = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";

            expectedUnsub.Should().Be(serializedUnsub);
        }

        [Test]
        public async Task Subscriptions_remove_after_closing_websockets_client()
        {
            string serializedLogs = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "logs");
            string logsId = serializedLogs.Substring(serializedLogs.Length - 44, 34);
            string expectedLogs = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", logsId, "\",\"id\":67}");
            expectedLogs.Should().Be(serializedLogs);


            string serializedNewPendingTx = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingTransactions");
            string newPendingTxId = serializedNewPendingTx.Substring(serializedNewPendingTx.Length - 44, 34);
            string expectedNewPendingTx =
                string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", newPendingTxId, "\",\"id\":67}");
            expectedNewPendingTx.Should().Be(serializedNewPendingTx);

            _jsonRpcDuplexClient.Closed += Raise.Event();

            string serializedLogsUnsub = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_unsubscribe", logsId);
            string expectedLogsUnsub =
                string.Concat("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Failed to unsubscribe: ",
                    logsId, ".\",\"data\":false},\"id\":67}");
            expectedLogsUnsub.Should().Be(serializedLogsUnsub);

            string serializedNewPendingTxUnsub = await RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_unsubscribe", newPendingTxId);
            string expectedNewPendingTxUnsub =
                string.Concat("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Failed to unsubscribe: ",
                    newPendingTxId, ".\",\"data\":false},\"id\":67}");
            expectedNewPendingTxUnsub.Should().Be(serializedNewPendingTxUnsub);
        }

        [Test]
        public void LogsSubscription_can_send_logs_with_removed_txs_when_inserted()
        {
            int blockNumber = 55555;
            Filter filter = Substitute.For<Filter>();

            LogsSubscription logsSubscription = new(_jsonRpcDuplexClient, _receiptCanonicalityMonitor, _filterStore, _blockTree, _logManager, filter);

            LogEntry logEntry = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            TxReceipt[] txReceipts = { Build.A.Receipt.WithLogs(logEntry).WithBlockNumber(blockNumber).TestObject };
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
            Block block = Build.A.Block.TestObject;
            Block previousBlock = Build.A.Block.WithHeader(blockHeader).WithBloom(new Bloom(txReceipts.Select(r => r.Bloom).ToArray())).TestObject;
            _receiptStorage.Get(Arg.Any<Block>()).Returns(txReceipts);
            List<JsonRpcResult> jsonRpcResults = new();

            ManualResetEvent manualResetEvent = new(false);
            BlockReplacementEventArgs blockEventArgs = new(block, previousBlock);
            logsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResults.Add(j);
                manualResetEvent.Set();
            }));

            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockEventArgs);
            _receiptStorage.ReceiptsInserted += Raise.EventWith(new object(), blockEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(2000));

            jsonRpcResults.Count.Should().Be(1);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", logsSubscription.Id, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":true,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }
    }
}
