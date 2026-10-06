// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.JsonRpc.WebSockets;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Facade.Eth;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.Modules;
using Nethermind.Core.Memory;
using Nethermind.Serialization.Json;
using System.IO.Abstractions;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DebugModuleTests
{
    private readonly IJsonRpcConfig _jsonRpcConfig = new JsonRpcConfig();
    private readonly ISpecProvider _specProvider = SpecProviderSubstitute.Create();
    private readonly IDebugBridge _debugBridge = Substitute.For<IDebugBridge>();
    private readonly IBlockFinder _blockFinder = Substitute.For<IBlockFinder>();
    private readonly IBlockchainBridge _blockchainBridge = Substitute.For<IBlockchainBridge>();
    private readonly MemDb _blocksDb = new();

    private DebugRpcModule CreateModule(IJsonRpcConfig? config = null) => new(
        LimboLogs.Instance,
        _debugBridge,
        config ?? _jsonRpcConfig,
        _specProvider,
        _blockchainBridge,
        new BlocksConfig(),
        _blockFinder,
        new BlockForRpcFactory());

    private Task<JsonRpcResponse> Request(string method, params object?[]? parameters) =>
        RpcTest.TestRequest<IDebugRpcModule>(CreateModule(), method, parameters);

    private Task<string> SerializedRequest(string method, params object?[]? parameters) =>
        RpcTest.TestSerializedRequest<IDebugRpcModule>(CreateModule(), method, parameters);

    private BlockTree BuildBlockTree(Func<BlockTreeBuilder, BlockTreeBuilder>? builderOptions = null)
    {
        BlockTreeBuilder builder = Build.A.BlockTree().WithBlocksDb(_blocksDb).WithBlockStore(new BlockStore(_blocksDb));
        builder = builderOptions?.Invoke(builder) ?? builder;
        return builder.TestObject;
    }

    private ResultWrapper<IEnumerable<string>> StandardTraceToFile(DebugRpcModule rpcModule, bool isBadBlock, Hash256 blockHash) =>
        isBadBlock
            ? rpcModule.debug_standardTraceBadBlockToFile(blockHash)
            : rpcModule.debug_standardTraceBlockToFile(blockHash);

    [Test]
    public async Task DebugGetFromDb_WhenValueExists_ReturnsValue()
    {
        byte[] key = [1, 2, 3];
        byte[] value = [4, 5, 6];
        _debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(value);

        using JsonRpcResponse response = await Request("debug_getFromDb", "STATE", key);
        RpcTest.AssertSuccess<byte[]>(response);
    }

    [Test]
    public async Task DebugGetFromDb_WhenValueIsNull_ReturnsSuccess()
    {
        _debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns((byte[])null!);
        byte[] key = [1, 2, 3];

        using JsonRpcResponse response = await Request("debug_getFromDb", "STATE", key);
        RpcTest.AssertSuccess<byte[]>(response);
    }

    [TestCase("0x1")]
    public async Task DebugGetChainLevel_WhenLevelExists_ReturnsChainLevel(object parameter)
    {
        _debugBridge.GetLevelInfo(1).Returns(new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1000), new BlockInfo(TestItem.KeccakB, 1001)));

        using JsonRpcResponse response = await Request("debug_getChainLevel", parameter);
        ChainLevelForRpc? chainLevel = RpcTest.AssertSuccess<ChainLevelForRpc>(response);
        Assert.That(chainLevel, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainLevel?.HasBlockOnMainChain, Is.True);
            Assert.That(chainLevel?.BlockInfos.Length, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task DebugGetRawHeader_WhenBlockExists_ReturnsHeaderRlp()
    {
        Block block = Build.A.Block.WithNumber(0).TestObject;
        _debugBridge.GetBlock(new BlockParameter(0UL)).Returns(block);

        using JsonRpcResponse response = await Request("debug_getRawHeader", "0x0");
        Assert.That(RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan(), Is.SequenceEqualTo(new HeaderDecoder().Encode(block.Header).Bytes));
    }

    [TestCaseSource(nameof(RawBlockCases))]
    public async Task DebugGetRawBlock_WhenBlockExists_ReturnsBlockRlp(object requestParameter, BlockParameter blockParameter)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        _debugBridge.GetBlock(blockParameter).Returns(block);

        using JsonRpcResponse response = await Request("debug_getRawBlock", requestParameter);
        Assert.That(RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan(), Is.SequenceEqualTo(new BlockDecoder().Encode(block).Bytes));
    }

    [Test]
    public async Task DebugGetRawTransaction_WhenTransactionExists_ReturnsTransactionRlp()
    {
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        string expected = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true);
        _debugBridge.GetTransactionFromHash(transaction.Hash!).Returns(transaction);

        string serialized = await SerializedRequest("debug_getRawTransaction", transaction.Hash!);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expected}\",\"id\":67}}"));
    }

    [Test]
    public async Task DebugGetRawTransaction_WhenTransactionNotFound_ReturnsNull()
    {
        _debugBridge.GetTransactionFromHash(Keccak.Zero).ReturnsNull();

        string serialized = await SerializedRequest("debug_getRawTransaction", Keccak.Zero);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [Test]
    public async Task DebugGetRawReceipts_WhenReceiptsExist_ReturnsHexArray()
    {
        TxReceipt[] receipts = [Build.A.Receipt.TestObject, Build.A.Receipt.TestObject];
        _debugBridge.GetReceiptsForBlock(new BlockParameter(1L)).Returns(receipts);
        RlpBehaviors behavior = (_specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        string expected = $"[\"{Rlp.Encode(receipts[0], behavior).Bytes.ToHexString(true)}\",\"{Rlp.Encode(receipts[1], behavior).Bytes.ToHexString(true)}\"]";

        string serialized = await SerializedRequest("debug_getRawReceipts", "0x1");
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expected},\"id\":67}}"));
    }

    [Test]
    public async Task DebugGetRawReceipts_WhenNoReceipts_ReturnsEmptyArray()
    {
        _debugBridge.GetReceiptsForBlock(new BlockParameter(1L)).Returns([]);

        string serialized = await SerializedRequest("debug_getRawReceipts", "0x1");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    [TestCaseSource(nameof(RawMissingCases))]
    public async Task DebugGetRaw_WhenResourceMissing_ReturnsResourceNotFound(Action<IDebugBridge> setup, string method, object parameter)
    {
        setup(_debugBridge);

        using JsonRpcResponse response = await Request(method, parameter);
        Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.ResourceNotFound));
    }

    [Test]
    public async Task DebugGetRawBlockAccessList_WhenAvailable_ReturnsRlp()
    {
        Block block = Build.A.Block.WithNumber(1).WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp).TestObject;
        byte[] rawBal = [0xc0];
        _debugBridge.GetBlock(BlockParameter.Latest).Returns(block);
        _blockchainBridge.GetBlockAccessListRlp(block.Number, block.Hash!).Returns(ArrayMemoryManager.From(rawBal));

        string serialized = await SerializedRequest("debug_getRawBlockAccessList", "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xc0\",\"id\":67}"));
    }

    [TestCaseSource(nameof(RawBlockAccessListErrorCases))]
    public async Task DebugGetRawBlockAccessList_WhenUnavailable_ReturnsError(Action<IDebugBridge, IBlockchainBridge> setup, int expectedErrorCode)
    {
        setup(_debugBridge, _blockchainBridge);

        using JsonRpcResponse response = await Request("debug_getRawBlockAccessList", "latest");
        Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(expectedErrorCode));
    }

    [Test]
    public void DebugTraceCallMany_WhenResultStreamed_DoesNotEnumerateBeyondFirstBundle()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;
        _blockFinder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).ReturnsForAnyArgs(header);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _debugBridge
            .GetBundleTraces(Arg.Any<TransactionBundle[]>(), Arg.Any<BlockParameter>(), Arg.Any<ulong?>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(static c => StreamBundles(c.ArgAt<CancellationToken>(3)));

        DebugRpcModule rpcModule = CreateModule();
        TransactionBundle bundle = new() { Transactions = [new LegacyTransactionForRpc { To = TestItem.AddressC }] };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result =
            rpcModule.debug_traceCallMany([bundle, bundle], BlockParameter.Latest, new GethTraceOptions { Tracer = "callTracer" });

        // The first inner sequence touches WaitHandle (throws ObjectDisposedException if the
        // timeout CTS has been disposed). The second bundle throws unconditionally, so the
        // call only succeeds if the result is a deferred sequence and we stop after the first.
        using IEnumerator<IEnumerable<GethLikeTxTrace>> outer = result.Data.GetEnumerator();
        Assert.That(outer.MoveNext(), Is.True);
        Assert.That(outer.Current.Count(), Is.EqualTo(1));
    }

    [Test]
    public void DebugTraceCall_WhenTracerProvided_ReturnsTrace()
    {
        GethTxTraceEntry entry = new()
        {
            Storage = new Dictionary<UInt256, UInt256>
            {
                {UInt256.Parse("1".PadLeft(64, '0')), UInt256.Parse("2".PadLeft(64, '0'))},
                {UInt256.Parse("3".PadLeft(64, '0')), UInt256.Parse("4".PadLeft(64, '0'))},
            },
            Memory = (ReadOnlyMemory<byte>?)new byte[64]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6
            },
            Stack = null,
            Opcode = "STOP",
            Gas = 22000,
            GasCost = 1,
            Depth = 1
        };

        GethLikeTxTrace trace = new()
        {
            ReturnValue = Bytes.FromHexString("a2")
        };
        trace.Entries.Add(entry);

        // Non-empty Tracer keeps debug_traceCall on the buffered path; struct-log default streams.
        GethTraceOptions gtOptions = new() { Tracer = "callTracer" };

        Transaction transaction = Build.A.Transaction.WithTo(TestItem.AddressA).WithHash(TestItem.KeccakA).TestObject;

        _debugBridge.GetTransactionTrace(Arg.Any<Transaction>(), Arg.Any<BlockParameter>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>()).Returns(trace);
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(1).TestObject);
        _blockFinder.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>()).ReturnsForAnyArgs(Build.A.BlockHeader.WithNumber(1).TestObject);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).ReturnsForAnyArgs(Build.A.BlockHeader.WithNumber(1).TestObject);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        DebugRpcModule rpcModule = CreateModule();
        ResultWrapper<GethLikeTxTrace> debugTraceCall = rpcModule.debug_traceCall(TransactionForRpc.FromTransaction(transaction), null, gtOptions);
        ResultWrapper<GethLikeTxTrace> expected = ResultWrapper<GethLikeTxTrace>.Success(
            new GethLikeTxTrace
            {
                Failed = false,
                Entries =
                [
                    new GethTxTraceEntry
                    {
                        Gas = 22000,
                        GasCost = 1,
                        Depth = 1,
                        Memory = (ReadOnlyMemory<byte>?)new byte[64]
                        {
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6
                        },
                        Opcode = "STOP",
                        ProgramCounter = 0,
                        Stack = null,
                        Storage = new Dictionary<UInt256, UInt256>
                        {
                            {
                                UInt256.Parse("0000000000000000000000000000000000000000000000000000000000000001"),
                                UInt256.Parse("0000000000000000000000000000000000000000000000000000000000000002")
                            },
                            {
                                UInt256.Parse("0000000000000000000000000000000000000000000000000000000000000003"),
                                UInt256.Parse("0000000000000000000000000000000000000000000000000000000000000004")
                            },
                        }
                    }
                ],
                Gas = 0,
                ReturnValue = [162]
            }
        );

        Assert.That(debugTraceCall.Result, Is.EqualTo(expected.Result));
        Assert.That(debugTraceCall.ErrorCode, Is.EqualTo(expected.ErrorCode));
        Assert.That(JToken.Parse(JsonSerializer.Serialize(debugTraceCall.Data)), Is.EqualTo(JToken.Parse(JsonSerializer.Serialize(expected.Data))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void DebugStandardTraceBlockToFile_WhenStateAvailable_ReturnsFileNames([Values] bool isBadBlock)
    {
        Hash256 blockHash = Keccak.EmptyTreeHash;

        static IEnumerable<string> GetFileNames(Hash256 hash) =>
            new[] { $"block_{hash.ToShortString()}-0", $"block_{hash.ToShortString()}-1" };

        BlockHeader header = Build.A.BlockHeader.WithHash(blockHash).TestObject;
        _blockFinder.FindHeader(blockHash).Returns(header);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        if (isBadBlock)
        {
            _debugBridge
                .TraceBadBlockToFile(Arg.Is(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(static c => GetFileNames(c.ArgAt<Hash256>(0)));
        }
        else
        {
            _debugBridge
                .TraceBlockToFile(Arg.Is(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(static c => GetFileNames(c.ArgAt<Hash256>(0)));
        }

        ResultWrapper<IEnumerable<string>> actual = StandardTraceToFile(CreateModule(), isBadBlock, blockHash);

        Assert.That(actual.Result, Is.EqualTo(Result.Success));
        Assert.That(actual.ErrorCode, Is.Zero);
        Assert.That(actual.Data, Is.EqualTo(GetFileNames(blockHash)));
    }

    [Test]
    public void DebugStandardTraceBlockToFile_WhenBlockMissing_ReturnsResourceNotFound([Values] bool isBadBlock)
    {
        Hash256 blockHash = TestItem.KeccakA;
        _blockFinder.FindHeader(blockHash).ReturnsNull();

        ResultWrapper<IEnumerable<string>> actual = StandardTraceToFile(CreateModule(), isBadBlock, blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
        Assert.That(actual.Result.Error, Does.Contain("Cannot find header"));
    }

    [Test]
    public void DebugStandardTraceBlockToFile_WhenStateUnavailable_ReturnsResourceUnavailable([Values] bool isBadBlock)
    {
        Hash256 blockHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithHash(blockHash).WithNumber(100).TestObject;

        _blockFinder.FindHeader(blockHash).Returns(header);
        _blockchainBridge.HasStateForBlock(Arg.Is(header)).Returns(false);

        ResultWrapper<IEnumerable<string>> actual = StandardTraceToFile(CreateModule(), isBadBlock, blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
        Assert.That(actual.Result.Error, Does.Contain("No state available"));
    }

    [Test]
    public void DebugIntermediateRoots_WhenStateAvailable_ReturnsRoots()
    {
        Hash256 blockHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;
        _blockFinder.FindHeader(blockHash).Returns(header);
        _blockchainBridge.HasStateForBlock(Arg.Is(header)).Returns(true);

        Hash256[] expected = [TestItem.KeccakB, TestItem.KeccakC];
        _debugBridge
            .GetBlockIntermediateRoots(Arg.Is(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions?>())
            .Returns(expected);

        ResultWrapper<IReadOnlyCollection<Hash256>> actual = CreateModule().debug_intermediateRoots(blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(actual.Data, Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(IntermediateRootsErrorCases))]
    public void DebugIntermediateRoots_WhenBlockOrStateMissing_ReturnsError(
        Action<Hash256, IBlockFinder, IBlockchainBridge> setup,
        int expectedErrorCode,
        string? expectedErrorSubstring)
    {
        Hash256 blockHash = TestItem.KeccakA;
        setup(blockHash, _blockFinder, _blockchainBridge);

        ResultWrapper<IReadOnlyCollection<Hash256>> actual = CreateModule().debug_intermediateRoots(blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(expectedErrorCode));
        if (expectedErrorSubstring is not null)
        {
            Assert.That(actual.Result.Error, Does.Contain(expectedErrorSubstring));
        }
    }

    [TestCaseSource(nameof(TraceBaseStateGuardErrorCases))]
    public void DebugTraceTransactionByIndex_WhenTraceBaseStateGuardRejects_ReturnsResourceUnavailable(
        Func<DebugRpcModule, BlockHeader, ResultWrapper<GethLikeTxTrace>> invoke,
        Action<BlockHeader, BlockHeader, IBlockFinder, IBlockchainBridge> setup,
        string expectedErrorSubstring)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;

        setup(header, parent, _blockFinder, _blockchainBridge);

        ResultWrapper<GethLikeTxTrace> actual = invoke(CreateModule(), header);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
        Assert.That(actual.Result.Error, Does.Contain(expectedErrorSubstring));
    }

    [Test]
    public async Task TraceChain_orders_results_skips_empty_intermediate_blocks_and_keeps_terminal([Values] bool tracerError, [Values] bool useTags)
    {
        SubscriptionManager manager = new(new SubscriptionFactory(), LimboLogs.Instance);
        IJsonRpcConfig config = Substitute.For<IJsonRpcConfig>();
        config.Timeout.Returns(1);
        DebugRpcModule module = CreateTraceChainModule(manager, config);
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("trace-chain-client");
        using JsonRpcContext context = new(RpcEndpoint.Ws, client);
        Assert.That(JsonRpcContext.Current.Value, Is.SameAs(context));
        SetUpTraceChain();
        List<string> notifications = [];
        client.SendJsonRpcResult(Arg.Any<JsonRpcResult>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            notifications.Add(RpcTest.SerializeResponse(call.Arg<JsonRpcResult>().Response));
            return Task.FromResult(0);
        });
        List<ulong> replayed = [];
        module.TraceChainReplay = (block, _, _) =>
        {
            replayed.Add(block.Number);
            return tracerError && block.Number == 1
                ? [new TraceChainTransaction(block.Transactions[0].Hash!, null, "tracer failed"), null]
                : ChainTraces(block);
        };

        using PendingTraceChainResponse response = (PendingTraceChainResponse)((IDebugSubscriptionRpcModule)module)
            .debug_subscribe("traceChain", useTags ? BlockParameter.Earliest : new BlockParameter(0UL), useTags ? BlockParameter.Latest : new BlockParameter(4UL));
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        response.Subscription.OwnLease(() => released.SetResult());
        response.Subscription.ReplayModule = module;
        Assert.That(notifications, Is.Empty);
        response.TakeActivation().Activate();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = config.DidNotReceive().Timeout;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notifications, Has.Count.EqualTo(3));
            Assert.That(replayed, Is.EqualTo(new ulong[] { 1, 2, 3, 4 }));
        }
        ulong[] expectedBlocks = [1, 3, 4];
        for (int index = 0; index < notifications.Count; index++)
        {
            using JsonDocument notification = JsonDocument.Parse(notifications[index]);
            JsonElement root = notification.RootElement;
            JsonElement payload = root.GetProperty("params");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.GetProperty("method").GetString(), Is.EqualTo("debug_subscription"));
                Assert.That(root.TryGetProperty("id", out _), Is.False);
                Assert.That(payload.GetProperty("subscription").GetString(), Is.EqualTo(response.Data));
                Assert.That(payload.GetProperty("result").GetProperty("block").GetString(), Is.EqualTo($"0x{expectedBlocks[index]:x}"));
            }
            if (index == 0 && tracerError)
            {
                JsonElement traces = payload.GetProperty("result").GetProperty("traces");
                Assert.That(traces[0].GetProperty("error").GetString(), Is.EqualTo("tracer failed"));
                Assert.That(traces[1].ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
        }
        using ResultWrapper<bool> removed = ((IDebugSubscriptionRpcModule)module).debug_unsubscribe(response.Data);
        Assert.That(removed.Data, Is.True, "completed subscriptions remain explicitly unsubscribable");
    }

    [Test]
    public async Task TraceChain_holds_exclusive_lease_until_cancelled_or_completed(
        [Values("unsubscribe", "disconnect", "completion")] string finish, [Values] bool invalidTimeout)
    {
        SubscriptionManager manager = new(new SubscriptionFactory(), LimboLogs.Instance);
        SetUpTraceChain();
        List<ulong> replayed = [];
        IRpcModuleFactory<IDebugRpcModule> factory = Substitute.For<IRpcModuleFactory<IDebugRpcModule>>();
        factory.Create().Returns(_ =>
        {
            DebugRpcModule module = CreateTraceChainModule(manager);
            module.TraceChainReplay = (block, _, _) =>
            {
                replayed.Add(block.Number);
                return ChainTraces(block);
            };
            return module;
        });
        BoundedModulePool<IDebugRpcModule> pool = new(factory, 1, 10_000);
        JsonRpcConfig config = new() { EnabledModules = [ModuleType.Debug] };
        RpcModuleProvider provider = new(Substitute.For<IFileSystem>(), config, new EthereumJsonSerializer(), LimboLogs.Instance);
        provider.Register(pool);
        using GCKeeper keeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
        JsonRpcService service = new(provider, LimboLogs.Instance, config, keeper);
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("lease-client");
        using JsonRpcContext context = new(RpcEndpoint.Ws, client);
        TaskCompletionSource sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SendJsonRpcResult(Arg.Any<JsonRpcResult>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            sending.TrySetResult();
            await allowSend.Task.WaitAsync(call.Arg<CancellationToken>());
            return 0;
        });
        using JsonRpcResponse response = await service.SendRequestAsync(RpcTest.BuildJsonRequest("debug_subscribe", "traceChain", "0x0", "0x4",
            new { timeout = invalidTimeout ? "bad" : null }), context);
        string id = RpcTest.AssertSuccess<string>(response);
        IDebugRpcModule beforeAck = await pool.GetModule(false);
        pool.ReturnModule(beforeAck);
        using TraceChainSubscription subscription = ((PendingTraceChainResponse)response).TakeActivation();
        subscription.Activate();
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<IDebugRpcModule> waiting = pool.GetModule(false);
        Assert.That(waiting.IsCompleted, Is.False);
        Assert.That(replayed, Is.EqualTo(new ulong[] { 1 }), "the next block must wait for the previous send");
        IJsonRpcDuplexClient otherClient = Substitute.For<IJsonRpcDuplexClient>();
        otherClient.Id.Returns("other-client");
        using JsonRpcContext otherContext = new(RpcEndpoint.Ws, otherClient);
        using JsonRpcResponse denied = await service.SendRequestAsync(RpcTest.BuildJsonRequest("debug_unsubscribe", id), otherContext);
        Assert.That(RpcTest.AssertSuccess<bool>(denied), Is.False);
        if (finish == "disconnect") client.Closed += Raise.Event<EventHandler>(client, EventArgs.Empty);
        else if (finish == "unsubscribe")
        {
            using JsonRpcResponse unsubscribe = await service.SendRequestAsync(RpcTest.BuildJsonRequest("debug_unsubscribe", id), context);
            Assert.That(RpcTest.AssertSuccess<bool>(unsubscribe), Is.True);
        }
        else allowSend.SetResult();
        IDebugRpcModule returned = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        pool.ReturnModule(returned);
        manager.RemoveClientSubscriptions(client);
    }

    [Test]
    public async Task TraceChain_batch_registers_without_holding_exclusive_rentals([Values] bool secondSubscription)
    {
        SubscriptionManager manager = new(new SubscriptionFactory(), LimboLogs.Instance);
        SetUpTraceChain();
        IRpcModuleFactory<IDebugRpcModule> factory = Substitute.For<IRpcModuleFactory<IDebugRpcModule>>();
        factory.Create().Returns(_ => CreateTraceChainModule(manager));
        BoundedModulePool<IDebugRpcModule> pool = new(factory, 1, 0);
        JsonRpcConfig config = new() { EnabledModules = [ModuleType.Debug] };
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        RpcModuleProvider provider = new(fileSystem, config, new EthereumJsonSerializer(), LimboLogs.Instance);
        provider.Register(pool);
        using GCKeeper keeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
        JsonRpcService service = new(provider, LimboLogs.Instance, config, keeper);
        JsonRpcProcessor processor = new(service, config, fileSystem, LimboLogs.Instance);
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("batch-lease-client");
        using JsonRpcContext context = new(RpcEndpoint.IPC, client);
        using MemoryMessageStream stream = new();
        using SocketSendLock sendLock = new();
        using SocketJsonRpcResponseSink<MemoryMessageStream> sink = new(stream, new NullJsonRpcLocalStats(), null, sendLock, context);
        TaskCompletionSource<bool> notifiedAfterBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SendJsonRpcResult(Arg.Any<JsonRpcResult>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            byte[] bytes = stream.ToArray();
            notifiedAfterBatch.TrySetResult(bytes.Length >= 2 && bytes[^2] == (byte)']' && bytes[^1] == (byte)'\n');
            return Task.FromResult(0);
        });
        using JsonRpcResponse control = await service.SendRequestAsync(
            RpcTest.BuildJsonRequest("debug_traceBlockByHash", TestItem.KeccakD), context);
        Assert.That(RpcTest.AssertError(control).Code, Is.EqualTo(ErrorCodes.ResourceNotFound), "exclusive rental works before the batch");
        string second = secondSubscription
            ? """{"jsonrpc":"2.0","id":2,"method":"debug_subscribe","params":["traceChain","0x0","0x4"]}"""
            : $$"""{"jsonrpc":"2.0","id":2,"method":"debug_traceBlockByHash","params":["{{TestItem.KeccakD}}"]}""";
        string batch = """[{"jsonrpc":"2.0","id":1,"method":"debug_subscribe","params":["traceChain","0x0","0x4"]},""" + second + "]";
        try
        {
            await processor.ProcessAsync(Encoding.UTF8.GetBytes(batch), context, sink,
                new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument));
            using JsonDocument response = JsonDocument.Parse(stream.ToArray());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(2));
                Assert.That(response.RootElement[0].GetProperty("result").GetString(), Does.StartWith("0x"));
                if (secondSubscription)
                    Assert.That(response.RootElement[1].GetProperty("result").GetString(), Does.StartWith("0x"));
                else
                    Assert.That(response.RootElement[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.ResourceNotFound));
            }
            Assert.That(await notifiedAfterBatch.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.True,
                "no producer may start before the complete batch acknowledgement");
        }
        finally
        {
            manager.RemoveClientSubscriptions(client);
        }
    }

    [Test]
    public async Task TraceChain_cancelled_pending_rental_frees_queue_capacity([Values] bool acknowledge)
    {
        SubscriptionManager manager = new(new SubscriptionFactory(), LimboLogs.Instance);
        SetUpTraceChain();
        IRpcModuleFactory<IDebugRpcModule> factory = Substitute.For<IRpcModuleFactory<IDebugRpcModule>>();
        factory.Create().Returns(_ => CreateTraceChainModule(manager));
        BoundedModulePool<IDebugRpcModule> bounded = new(factory, 1, 10_000, new RpcLimits(queuedLimit: 1, sharedLimit: 1));
        ObservedExclusivePool pool = new(bounded);
        JsonRpcConfig config = new() { EnabledModules = [ModuleType.Debug] };
        RpcModuleProvider provider = new(Substitute.For<IFileSystem>(), config, new EthereumJsonSerializer(), LimboLogs.Instance);
        provider.Register<IDebugRpcModule>(pool);
        using GCKeeper keeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
        JsonRpcService service = new(provider, LimboLogs.Instance, config, keeper);
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("saturated-pool-client");
        using JsonRpcContext context = new(RpcEndpoint.IPC, client);
        IDebugRpcModule? held = await pool.GetModule(false);
        try
        {
            using JsonRpcResponse response = await service.SendRequestAsync(
                RpcTest.BuildJsonRequest("debug_subscribe", "traceChain", "0x0", "0x4"), context);
            RpcTest.AssertSuccess(response);
            PendingTraceChainResponse pending = (PendingTraceChainResponse)response;
            Assert.That(pool.RentalStarted.Task.IsCompleted, Is.False, "no exclusive rental before acknowledgement");
            if (acknowledge)
            {
                pending.TakeActivation().Activate();
                await pool.RentalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using JsonRpcResponse unsubscribe = await service.SendRequestAsync(
                    RpcTest.BuildJsonRequest("debug_unsubscribe", pending.Data), context);
                Assert.That(RpcTest.AssertSuccess<bool>(unsubscribe), Is.True);
            }
            else response.Dispose();
            await pending.Subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(pool.RentalStarted.Task.IsCompleted, Is.EqualTo(acknowledge));

            Task<JsonRpcResponse> normal = service.SendRequestAsync(
                RpcTest.BuildJsonRequest("debug_traceBlockByHash", TestItem.KeccakD), context).AsTask();
            Assert.That(normal.IsCompleted, Is.False, "the cancelled subscription must not consume the only queue slot");
            pool.ReturnModule(held);
            held = null;
            using JsonRpcResponse normalResponse = await normal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(RpcTest.AssertError(normalResponse).Code, Is.EqualTo(ErrorCodes.ResourceNotFound));
        }
        finally
        {
            manager.RemoveClientSubscriptions(client);
            if (held is not null) pool.ReturnModule(held);
        }
    }

    [Test]
    public async Task TraceChain_cancellation_between_rental_and_transfer_returns_module_once()
    {
        SubscriptionManager manager = new(new SubscriptionFactory(), LimboLogs.Instance);
        SetUpTraceChain();
        bool replayed = false;
        IRpcModuleFactory<IDebugRpcModule> factory = Substitute.For<IRpcModuleFactory<IDebugRpcModule>>();
        factory.Create().Returns(_ =>
        {
            DebugRpcModule module = CreateTraceChainModule(manager);
            module.TraceChainReplay = (block, _, _) => { replayed = true; return ChainTraces(block); };
            return module;
        });
        ObservedExclusivePool pool = new(new BoundedModulePool<IDebugRpcModule>(factory, 1, 0));
        JsonRpcConfig config = new() { EnabledModules = [ModuleType.Debug] };
        RpcModuleProvider provider = new(Substitute.For<IFileSystem>(), config, new EthereumJsonSerializer(), LimboLogs.Instance);
        provider.Register<IDebugRpcModule>(pool);
        using GCKeeper keeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
        JsonRpcService service = new(provider, LimboLogs.Instance, config, keeper);
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("transfer-client");
        using JsonRpcContext context = new(RpcEndpoint.IPC, client);
        using JsonRpcResponse response = await service.SendRequestAsync(
            RpcTest.BuildJsonRequest("debug_subscribe", "traceChain", "0x0", "0x4"), context);
        RpcTest.AssertSuccess(response);
        PendingTraceChainResponse pending = (PendingTraceChainResponse)response;
        pool.BeforeTransfer = pending.Subscription.Abort;
        pending.TakeActivation().Activate();
        await pending.Subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.False);
            Assert.That(pool.ReturnCount, Is.EqualTo(2), "one shared registration return and one exclusive rental return");
        }
        IDebugRpcModule available = await pool.GetModule(false);
        pool.ReturnModule(available);
    }

    private sealed class ObservedExclusivePool(BoundedModulePool<IDebugRpcModule> inner) : IRpcModulePool<IDebugRpcModule>, IExclusiveRpcModulePool
    {
        internal TaskCompletionSource RentalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? BeforeTransfer { get; set; }
        internal int ReturnCount;
        public IRpcModuleFactory<IDebugRpcModule> Factory => inner.Factory;
        public bool SupportsExclusiveRental => true;
        public Task<IDebugRpcModule> GetModule(bool canBeShared) => inner.GetModule(canBeShared);
        public void ReturnModule(IDebugRpcModule module)
        {
            Interlocked.Increment(ref ReturnCount);
            inner.ReturnModule(module);
        }
        public ValueTask<IRpcModule> RentExclusive(CancellationToken cancellationToken)
        {
            ValueTask<IRpcModule> rental = ((IExclusiveRpcModulePool)inner).RentExclusive(cancellationToken);
            if (rental.IsCompletedSuccessfully) BeforeTransfer?.Invoke();
            RentalStarted.TrySetResult();
            return rental;
        }
    }

    [Test]
    public void TraceChain_rejects_invalid_ranges_and_subscription_names(
        [Values("traceChain", "unknown")] string name, [Values(0UL, 4UL, 5UL)] ulong start)
    {
        DebugRpcModule module = CreateTraceChainModule();
        SetUpTraceChain();
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("range-client");
        using JsonRpcContext context = new(RpcEndpoint.IPC, client);
        Assert.That(JsonRpcContext.Current.Value, Is.SameAs(context));
        using ResultWrapper<string> response = ((IDebugSubscriptionRpcModule)module).debug_subscribe(name, new BlockParameter(start), new BlockParameter(0UL));
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public void TraceChain_unknown_subscription_matches_reference_error()
    {
        DebugRpcModule module = CreateTraceChainModule();
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        using JsonRpcContext context = new(RpcEndpoint.IPC, client);
        Assert.That(JsonRpcContext.Current.Value, Is.SameAs(context));
        using ResultWrapper<string> response = ((IDebugSubscriptionRpcModule)module)
            .debug_subscribe("unknown", new BlockParameter(0UL), new BlockParameter(1UL));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.MethodNotFound));
            Assert.That(response.Result.Error, Is.EqualTo("no \"unknown\" subscription in debug namespace"));
        }
    }

    [Test]
    public async Task TraceChain_missing_block_or_parent_state_stops_and_releases([Values] bool missingBlock)
    {
        DebugRpcModule module = CreateTraceChainModule();
        SetUpTraceChain();
        if (missingBlock) _blockFinder.FindBlock(new BlockParameter(1UL)).ReturnsNull();
        else _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(false);
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("missing-state-client");
        using JsonRpcContext context = new(RpcEndpoint.IPC, client);
        Assert.That(JsonRpcContext.Current.Value, Is.SameAs(context));
        using PendingTraceChainResponse response = (PendingTraceChainResponse)((IDebugSubscriptionRpcModule)module)
            .debug_subscribe("traceChain", new BlockParameter(0UL), new BlockParameter(4UL));
        response.Subscription.ReplayModule = module;
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        response.Subscription.OwnLease(() => released.SetResult());
        response.TakeActivation().Activate();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DidNotReceive().SendJsonRpcResult(Arg.Any<JsonRpcResult>(), Arg.Any<CancellationToken>());
        response.Subscription.Abort();
    }

    [Test]
    public void TraceChain_options_ignore_call_only_fields_without_deserializing_them()
    {
        const string json = """
            {"enableMemory":true,"disableStack":true,"disableStorage":true,"enableReturnData":true,
             "limit":123,"tracer":"callTracer","timeout":"2s","tracerConfig":{"onlyTopCall":true},
             "txIndex":"invalid","stateOverrides":123,"blockOverrides":false,"noBaseFee":true,
             "txHash":"not-a-hash","logIndex":[],"streamMode":"invalid","disableMemory":true}
            """;
        TraceChainOptions parsed = JsonSerializer.Deserialize<TraceChainOptions>(json, EthereumJsonSerializer.JsonOptions)!;
        GethTraceOptions options = parsed.ToTraceOptions();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.EnableMemory, Is.True);
            Assert.That(options.DisableStack, Is.True);
            Assert.That(options.DisableStorage, Is.True);
            Assert.That(options.EnableReturnData, Is.True);
            Assert.That(options.Limit, Is.EqualTo(123));
            Assert.That(options.Tracer, Is.EqualTo("callTracer"));
            Assert.That(parsed.ParseTimeout(), Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(options.Timeout, Is.Null);
            Assert.That(options.TracerConfig!.Value.GetProperty("onlyTopCall").GetBoolean(), Is.True);
            Assert.That(options.TxHash, Is.Null);
            Assert.That(options.StateOverrides, Is.Null);
            Assert.That(options.BlockOverrides, Is.Null);
            Assert.That(options.NoBaseFee, Is.False);
            Assert.That(options.LogIndex, Is.Null);
            Assert.That(options.StreamMode, Is.Null);
        }
    }

    [TestCase("0", 0L)]
    [TestCase("1ns", 0L)]
    [TestCase("-1ns", 0L)]
    [TestCase("1.5s", 15000000L)]
    [TestCase("1m2s", 620000000L)]
    [TestCase("9223372036854775807ns", 92233720368547758L)]
    [TestCase("-9223372036854775808ns", -92233720368547758L)]
    public void TraceChain_duration_uses_go_units_and_range(string text, long ticks) =>
        Assert.That(new TraceChainOptions { Timeout = text }.ParseTimeout().Ticks, Is.EqualTo(ticks));

    [TestCase("00:00:01", "time: unknown unit \":\" in duration \"00:00:01\"")]
    [TestCase("9223372036854775808ns", "time: invalid duration \"9223372036854775808ns\"")]
    [TestCase("bad", "time: invalid duration \"bad\"")]
    [TestCase("1", "time: missing unit in duration \"1\"")]
    public void TraceChain_duration_rejects_non_go_formats(string text, string error) =>
        Assert.That(() => new TraceChainOptions { Timeout = text }.ParseTimeout(), Throws.TypeOf<FormatException>().With.Message.EqualTo(error));

    [Test]
    public void TraceChain_block_tracer_preserves_results_and_stops_at_first_error([Values(-1, 0, 1)] int failAt)
    {
        Block block = Build.A.Block.WithTransactions(
            Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject,
            Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject,
            Build.A.Transaction.WithHash(TestItem.KeccakC).TestObject).TestObject;
        List<GethLikeTxTrace> traces = [];
        List<IDisposable> resources = [];
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.BuildResult().Returns(traces);
        int index = 0;
        inner.When(t => t.EndTxTrace()).Do(_ =>
        {
            if (index == failAt) throw new InvalidOperationException("tracer failed");
            IDisposable resource = Substitute.For<IDisposable>();
            resources.Add(resource);
            traces.Add(new GethLikeTxTrace(resource) { TxHash = block.Transactions[index++].Hash });
        });
        TraceChainTransaction?[] result;
        using (TraceChainBlockTracer tracer = new(block, inner))
        {
            Exception? failure = null;
            foreach (Transaction transaction in block.Transactions)
            {
                tracer.StartNewTxTrace(transaction);
                try { tracer.EndTxTrace(); }
                catch (InvalidOperationException exception) { failure = exception; break; }
            }
            result = tracer.TakeResult(failure);
        }
        Assert.That(result, Has.Length.EqualTo(3));
        if (failAt >= 0)
        {
            Assert.That(result[failAt]!.Error, Is.EqualTo("tracer failed"));
            Assert.That(result.Skip(failAt + 1), Is.All.Null);
        }
        inner.Received(failAt < 0 ? 3 : failAt + 1).StartNewTxTrace(Arg.Any<Transaction>());
        foreach (IDisposable resource in resources) resource.DidNotReceive().Dispose();
        foreach (TraceChainTransaction? trace in result) trace?.Result?.Dispose();
        foreach (IDisposable resource in resources) resource.Received(1).Dispose();
    }

    [Test]
    public void TraceChain_aborted_block_disposes_completed_results()
    {
        IDisposable resource = Substitute.For<IDisposable>();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.BuildResult().Returns(new[] { new GethLikeTxTrace(resource) });
        new TraceChainBlockTracer(Build.A.Block.TestObject, inner).Dispose();
        resource.Received(1).Dispose();
    }

    [Test]
    public void TraceChain_transaction_timeout_is_distinct_from_subscription_cancellation()
    {
        Transaction transaction = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
        Block block = Build.A.Block.WithTransactions(transaction).TestObject;
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(transaction).Returns(NullTxTracer.Instance);
        using TraceChainBlockTracer tracer = new(block, inner, new TraceChainOptions { Timeout = "0" });
        using ITxTracer txTracer = tracer.StartNewTxTrace(transaction);
        Assert.Throws<OperationCanceledException>(() => tracer.EndTxTrace());
        Assert.That(tracer.TransactionTimedOut, Is.True);
    }

    [Test]
    public async Task TraceChain_refetches_blocks_by_number_after_acknowledgement()
    {
        DebugRpcModule module = CreateTraceChainModule();
        Block[] original = SetUpTraceChain();
        IJsonRpcDuplexClient client = Substitute.For<IJsonRpcDuplexClient>();
        client.Id.Returns("reorg-client");
        using JsonRpcContext context = new(RpcEndpoint.Ws, client);
        Assert.That(JsonRpcContext.Current.Value, Is.SameAs(context));
        using PendingTraceChainResponse response = (PendingTraceChainResponse)((IDebugSubscriptionRpcModule)module)
            .debug_subscribe("traceChain", new BlockParameter(0UL), new BlockParameter(1UL));
        Block replacement = Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(original[0].Header).WithHash(TestItem.KeccakD).TestObject)
            .WithTransactions(original[1].Transactions).TestObject;
        _blockFinder.FindBlock(new BlockParameter(1UL)).Returns(replacement);
        Block? replayed = null;
        module.TraceChainReplay = (block, _, _) => { replayed = block; return ChainTraces(block); };
        response.Subscription.ReplayModule = module;
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        response.Subscription.OwnLease(() => released.SetResult());
        response.TakeActivation().Activate();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(replayed, Is.SameAs(replacement));
        response.Subscription.Abort();
    }

    private DebugRpcModule CreateTraceChainModule(SubscriptionManager? manager = null, IJsonRpcConfig? config = null)
    {
        DebugRpcModule module = CreateModule(config);
        module.TraceChainSubscriptions = manager ?? new SubscriptionManager(new SubscriptionFactory(), LimboLogs.Instance);
        module.TraceChainReplay = static (block, _, _) => ChainTraces(block);
        return module;
    }

    private static TraceChainTransaction?[] ChainTraces(Block block) => block.Transactions
        .Select(tx => (TraceChainTransaction?)new TraceChainTransaction(tx.Hash!, new GethLikeTxTrace { TxHash = tx.Hash }, null)).ToArray();

    private Block[] SetUpTraceChain()
    {
        Block[] blocks = new Block[5];
        for (int index = 0; index < blocks.Length; index++)
        {
            Transaction[] transactions = index == 1
                ? [Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject, Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject]
                : index == 3 ? [Build.A.Transaction.WithHash(TestItem.KeccakC).TestObject] : [];
            BlockHeader header = index == 0
                ? Build.A.BlockHeader.WithNumber(0).TestObject
                : Build.A.BlockHeader.WithParent(blocks[index - 1].Header).TestObject;
            Block block = blocks[index] = Build.A.Block.WithHeader(header).WithTransactions(transactions).TestObject;
            _blockFinder.FindBlock(new BlockParameter((ulong)index)).Returns(block);
            _blockFinder.FindHeader(block.Hash!, BlockTreeLookupOptions.None, block.Number).Returns(block.Header);
        }
        _blockFinder.FindBlock(BlockParameter.Earliest).Returns(blocks[0]);
        _blockFinder.FindBlock(BlockParameter.Latest).Returns(blocks[^1]);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        return blocks;
    }

    [Test]
    public void DebugGetBadBlocks_WhenBadBlockStored_ReturnsBadBlock()
    {
        BadBlockStore badBlocksStore = null!;
        BlockTree blockTree = BuildBlockTree(b => b.WithBadBlockStore(badBlocksStore = new BadBlockStore(b.BadBlocksDb, 100)));

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(2).WithDifficulty(4).WithParent(block2).TestObject;

        blockTree.SuggestBlock(block0);
        blockTree.SuggestBlock(block1);
        blockTree.SuggestBlock(block2);
        blockTree.SuggestBlock(block3);

        blockTree.DeleteInvalidBlock(block1);

        BlockDecoder decoder = new();
        _blocksDb.Set(block1.Hash ?? new Hash256("0x0"), decoder.Encode(block1).Bytes);

        _debugBridge.GetBadBlocks().Returns(badBlocksStore.GetAll());

        AddBlockResult result = blockTree.SuggestBlock(block1);
        Assert.That(result, Is.EqualTo(AddBlockResult.InvalidBlock));

        ResultWrapper<IEnumerable<BadBlock>> blocks = CreateModule().debug_getBadBlocks();
        Assert.That(blocks.Data.Count(), Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Data.ElementAt(0).Hash, Is.EqualTo(block1.Hash));
            Assert.That(blocks.Data.ElementAt(0).Block.Difficulty, Is.EqualTo(new UInt256(2)));
        }
    }

    [Test]
    public async Task DebugMigrateReceipts_WhenInvoked_ReturnsBridgeResult()
    {
        _debugBridge.MigrateReceipts(Arg.Any<ulong>(), Arg.Any<ulong>()).Returns(true);

        // Both arguments are required. Hex-string quantities stay valid when a parallel fixture enables StrictHexFormat.
        string response = await SerializedRequest("debug_migrateReceipts", "0x64", "0xc8");

        Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}"));
        await _debugBridge.Received().MigrateReceipts(100, 200);
    }

    // EIP-8141: a frame transaction always executes at least one frame. A frames-less frame receipt
    // still encodes (as an empty frames list) but the decoder rejects it, so it must never be stored.
    [Test]
    public async Task DebugInsertReceipts_FrameTxReceiptWithoutFrames_IsRejectedAndNotStored(
        [Values(true, false)] bool nullFrames)
    {
        ReceiptForRpc receipt = FrameReceiptPayload(nullFrames ? null : []);

        ResultWrapper<bool> result = await CreateModule().debug_insertReceipts(new BlockParameter(1), [receipt]);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        _debugBridge.DidNotReceiveWithAnyArgs().InsertReceipts(default!, default!);
    }

    // The payer is written null-tolerantly and read strictly, so a payer-less frame receipt would persist and
    // then throw on every later read of its block's receipts. It is the same failure mode as a frames-less one.
    [Test]
    public async Task DebugInsertReceipts_FrameTxReceiptWithoutPayer_IsRejectedAndNotStored()
    {
        ReceiptForRpc receipt = FrameReceiptPayload(
            [new FrameReceiptForRpc { Status = TxFrameReceipt.StatusSuccess, ExecutionGasUsed = 21_000 }],
            withPayer: false);

        ResultWrapper<bool> result = await CreateModule().debug_insertReceipts(new BlockParameter(1), [receipt]);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        _debugBridge.DidNotReceiveWithAnyArgs().InsertReceipts(default!, default!);
    }

    // A null array entry is a caller error too: it otherwise dereferences into an internal error.
    [Test]
    public async Task DebugInsertReceipts_NullReceiptEntry_IsRejectedAndNotStored()
    {
        ResultWrapper<bool> result = await CreateModule().debug_insertReceipts(new BlockParameter(1), [null!]);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        _debugBridge.DidNotReceiveWithAnyArgs().InsertReceipts(default!, default!);
    }

    // The counterpart: the check must not stand between a well-formed frame receipt and the bridge.
    [Test]
    public async Task DebugInsertReceipts_FrameTxReceiptWithFrames_ReachesTheBridge()
    {
        ReceiptForRpc receipt = FrameReceiptPayload([
            new FrameReceiptForRpc { Status = TxFrameReceipt.StatusSuccess, ExecutionGasUsed = 21_000 },
            new FrameReceiptForRpc { Status = TxFrameReceipt.StatusFailure, ExecutionGasUsed = 30_000 }
        ]);
        TxReceipt[]? inserted = null;
        _debugBridge.WhenForAnyArgs(static b => b.InsertReceipts(default!, default!))
            .Do(call => inserted = call.Arg<TxReceipt[]>());

        ResultWrapper<bool> result = await CreateModule().debug_insertReceipts(new BlockParameter(1), [receipt]);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(inserted, Is.Not.Null);
        Assert.That(inserted![0].FrameReceipts!.Select(static f => (f.Status, f.ExecutionGasUsed)),
            Is.EqualTo(new[] { (TxFrameReceipt.StatusSuccess, 21_000UL), (TxFrameReceipt.StatusFailure, 30_000UL) }));
    }

    private static ReceiptForRpc FrameReceiptPayload(FrameReceiptForRpc[]? frameReceipts, bool withPayer = true) => new()
    {
        Type = TxType.FrameTx,
        CumulativeGasUsed = 21_000,
        Payer = withPayer ? TestItem.AddressA : null,
        FrameReceipts = frameReceipts,
        Logs = []
    };

    [Test]
    public async Task DebugResetHead_WhenInvoked_UpdatesHeadBlock([Values] bool updated)
    {
        _debugBridge.UpdateHeadBlock(TestItem.KeccakA).Returns(updated);

        string response = await SerializedRequest("debug_resetHead", TestItem.KeccakA);

        Assert.That(response, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{(updated ? "true" : "false")},\"id\":67}}"));
        _debugBridge.Received().UpdateHeadBlock(TestItem.KeccakA);
    }

    [Test]
    public async Task DebugSetHead_PassesParameterAndReportsRewindResult([Values] bool updated)
    {
        _debugBridge.UpdateHeadBlock(new BlockParameter(2UL)).Returns(updated);

        string response = await SerializedRequest("debug_setHead", "0x2");

        Assert.That(response, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{(updated ? "true" : "false")},\"id\":67}}"));
        _debugBridge.Received().UpdateHeadBlock(new BlockParameter(2UL));
    }

    [Test]
    public void DebugTraceTransactionInBlockByIndex_WithCustomTracer_DisposesDiscardedTracesAndPipelineDisposesSelectedTrace()
    {
        (IDisposable[] engines, GethLikeTxTrace[] traces) = CreateSentinelTraces(3);
        SetUpBlockTrace(traces);

        // A custom Tracer forces the non-streaming path (CanStreamStructLogs returns false when it's set),
        // which is where the rest of the block's traces would otherwise leak.
        GethTraceOptions options = new() { Tracer = "callTracer" };

        using (ResultWrapper<GethLikeTxTrace> result = CreateModule().debug_traceTransactionInBlockByIndex(BlockRlpFixture(3), 1, options))
        {
            Assert.That(result.Data, Is.SameAs(traces[1]));
            engines[1].DidNotReceive().Dispose();
        }

        using (Assert.EnterMultipleScope())
        {
            engines[0].Received(1).Dispose();
            engines[1].Received(1).Dispose();
            engines[2].Received(1).Dispose();
        }
    }

    [Test]
    public void DebugTraceTransactionInBlockByIndex_WithCustomTracer_WhenIndexOutOfRange_DisposesEveryTrace([Values(-1, 5)] int txIndex)
    {
        (IDisposable[] engines, GethLikeTxTrace[] traces) = CreateSentinelTraces(2);
        SetUpBlockTrace(traces);

        GethTraceOptions options = new() { Tracer = "callTracer" };

        using ResultWrapper<GethLikeTxTrace> result = CreateModule().debug_traceTransactionInBlockByIndex(BlockRlpFixture(2), txIndex, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            engines[0].Received(1).Dispose();
            engines[1].Received(1).Dispose();
        }
    }

    private static byte[] BlockRlpFixture(int txCount)
    {
        // Decoding the block RLP requires each transaction to carry a real signature - an unsigned
        // Transaction() fails RLP decode ("VRS is 0 length when decoding Transaction").
        Transaction[] transactions = new Transaction[txCount];
        for (int i = 0; i < txCount; i++)
        {
            transactions[i] = Build.A.Transaction.SignedAndResolved().TestObject;
        }

        return new BlockDecoder().Encode(Build.A.Block.WithNumber(1).WithTransactions(transactions).TestObject).Bytes;
    }

    private static (IDisposable[] Engines, GethLikeTxTrace[] Traces) CreateSentinelTraces(int count)
    {
        IDisposable[] engines = new IDisposable[count];
        GethLikeTxTrace[] traces = new GethLikeTxTrace[count];
        for (int i = 0; i < count; i++)
        {
            engines[i] = Substitute.For<IDisposable>();
            traces[i] = new GethLikeTxTrace(engines[i]);
        }

        return (engines, traces);
    }

    private void SetUpBlockTrace(GethLikeTxTrace[] traces)
    {
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        // Production backs the collection with a DisposableResettableList<GethLikeTxTrace> (BlockTracerBase.cs),
        // whose own Dispose() disposes every item a second time on top of GethLikeTxTraceCollection.DisposeItems()
        // - matching that here pins the invariant that the collection itself must stay undisposed (see the
        // why-comment in DebugRpcModule), rather than letting a plain array's no-op TryDispose() silently hide a
        // reintroduced double dispose.
        DisposableResettableList<GethLikeTxTrace> traceList = [.. traces];

        _debugBridge
            .GetBlockTrace(Arg.Any<Block>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(new GethLikeTxTraceCollection(traceList));
    }

    private static IEnumerable<IEnumerable<GethLikeTxTrace>> StreamBundles(CancellationToken token)
    {
        yield return YieldTrace(token);
        throw new InvalidOperationException("second bundle should not be enumerated — streaming was lost");
    }

    private static IEnumerable<GethLikeTxTrace> YieldTrace(CancellationToken token)
    {
        _ = token.WaitHandle;
        yield return new GethLikeTxTrace();
    }

    private static IEnumerable<TestCaseData> RawBlockCases()
    {
        yield return new TestCaseData("0x1", new BlockParameter(1L)) { TestName = "ByNumber" };
        yield return new TestCaseData(Keccak.Zero, new BlockParameter(Keccak.Zero)) { TestName = "ByHash" };
        yield return new TestCaseData("latest", BlockParameter.Latest) { TestName = "ByName" };
    }

    private static IEnumerable<TestCaseData> RawMissingCases()
    {
        yield return new TestCaseData(
            (Action<IDebugBridge>)(b => b.GetBlock(new BlockParameter(1L)).ReturnsNull()),
            "debug_getRawBlock", (object)"0x1")
        { TestName = "RawBlock_ByNumber" };
        yield return new TestCaseData(
            (Action<IDebugBridge>)(b => b.GetBlock(new BlockParameter(Keccak.Zero)).ReturnsNull()),
            "debug_getRawBlock", (object)Keccak.Zero)
        { TestName = "RawBlock_ByHash" };
    }

    private static IEnumerable<TestCaseData> RawBlockAccessListErrorCases()
    {
        yield return new TestCaseData(
            (Action<IDebugBridge, IBlockchainBridge>)((debug, _) => debug.GetBlock(BlockParameter.Latest).ReturnsNull()),
            ErrorCodes.BlockAccessListResourceNotFound)
        { TestName = "missing_block" };

        yield return new TestCaseData(
            (Action<IDebugBridge, IBlockchainBridge>)((debug, _) =>
                debug.GetBlock(BlockParameter.Latest).Returns(Build.A.Block.WithNumber(1).TestObject)),
            ErrorCodes.BlockAccessListResourceNotFound)
        { TestName = "unavailable_before_fork" };

        yield return new TestCaseData(
            (Action<IDebugBridge, IBlockchainBridge>)((debug, chain) =>
            {
                Block block = Build.A.Block.WithNumber(1).WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp).TestObject;
                debug.GetBlock(BlockParameter.Latest).Returns(block);
                chain.GetBlockAccessListRlp(block.Number, block.Hash!).ReturnsNull();
            }),
            ErrorCodes.PrunedHistoryUnavailable)
        { TestName = "pruned" };
    }

    private static IEnumerable<TestCaseData> IntermediateRootsErrorCases()
    {
        yield return new TestCaseData(
            (Action<Hash256, IBlockFinder, IBlockchainBridge>)((blockHash, finder, _) =>
                finder.FindHeader(blockHash).ReturnsNull()),
            ErrorCodes.ResourceNotFound,
            "Cannot find header")
        { TestName = "block_not_found" };

        yield return new TestCaseData(
            (Action<Hash256, IBlockFinder, IBlockchainBridge>)((blockHash, finder, bridge) =>
            {
                BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;
                finder.FindHeader(blockHash).Returns(header);
                bridge.HasStateForBlock(Arg.Is(header)).Returns(false);
            }),
            ErrorCodes.ResourceUnavailable,
            null)
        { TestName = "state_unavailable" };
    }

    private static IEnumerable<TestCaseData> TraceBaseStateGuardErrorCases()
    {
        (string Name, Func<DebugRpcModule, BlockHeader, ResultWrapper<GethLikeTxTrace>> Invoke, Action<BlockHeader, IBlockFinder> ResolveHeader)[] methods =
        [
            (
                "ByBlockAndIndex",
                static (module, header) => module.debug_traceTransactionByBlockAndIndex(new BlockParameter(header.Number), 0),
                static (header, finder) =>
                {
                    finder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
                    finder.FindHeader(Arg.Any<BlockParameter>()).ReturnsForAnyArgs(header);
                }
            ),
            (
                "ByBlockhashAndIndex",
                static (module, header) => module.debug_traceTransactionByBlockhashAndIndex(header.Hash!, 0),
                static (header, finder) => finder.FindHeader(header.Hash!).Returns(header)
            )
        ];

        foreach ((string name, Func<DebugRpcModule, BlockHeader, ResultWrapper<GethLikeTxTrace>> invoke, Action<BlockHeader, IBlockFinder> resolveHeader) in methods)
        {
            yield return new TestCaseData(
                invoke,
                (Action<BlockHeader, BlockHeader, IBlockFinder, IBlockchainBridge>)((header, _, finder, _) =>
                {
                    resolveHeader(header, finder);
                    finder.FindHeader(header.ParentHash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).ReturnsNull();
                }),
                "Cannot find parent header")
            { TestName = $"{name}_parent_header_missing" };

            yield return new TestCaseData(
                invoke,
                (Action<BlockHeader, BlockHeader, IBlockFinder, IBlockchainBridge>)((header, parent, finder, bridge) =>
                {
                    resolveHeader(header, finder);
                    finder.FindHeader(header.ParentHash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);
                    // Positive control: the block's own state reports available, so a guard that (incorrectly)
                    // checked the block's own state instead of its parent's would let this request through.
                    bridge.HasStateForBlock(Arg.Is(header)).Returns(true);
                    bridge.HasStateForBlock(Arg.Is(parent)).Returns(false);
                }),
                "No state available for block")
            { TestName = $"{name}_parent_state_missing" };
        }
    }
}
