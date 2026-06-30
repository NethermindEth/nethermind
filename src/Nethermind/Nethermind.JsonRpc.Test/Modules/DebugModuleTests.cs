// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
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

    private DebugRpcModule CreateModule() => new(
        LimboLogs.Instance,
        _debugBridge,
        _jsonRpcConfig,
        _specProvider,
        _blockchainBridge,
        new BlocksConfig(),
        _blockFinder);

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
        Assert.That(RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan().ToArray(), Is.EqualTo(new HeaderDecoder().Encode(block.Header).Bytes));
    }

    [TestCaseSource(nameof(RawBlockCases))]
    public async Task DebugGetRawBlock_WhenBlockExists_ReturnsBlockRlp(object requestParameter, BlockParameter blockParameter)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        _debugBridge.GetBlock(blockParameter).Returns(block);

        using JsonRpcResponse response = await Request("debug_getRawBlock", requestParameter);
        Assert.That(RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan().ToArray(), Is.EqualTo(new BlockDecoder().Encode(block).Bytes));
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

    [TestCase(false)]
    [TestCase(true)]
    public void DebugStandardTraceBlockToFile_WhenStateAvailable_ReturnsFileNames(bool isBadBlock)
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

    [TestCase(false)]
    [TestCase(true)]
    public void DebugStandardTraceBlockToFile_WhenBlockMissing_ReturnsResourceNotFound(bool isBadBlock)
    {
        Hash256 blockHash = TestItem.KeccakA;
        _blockFinder.FindHeader(blockHash).ReturnsNull();

        ResultWrapper<IEnumerable<string>> actual = StandardTraceToFile(CreateModule(), isBadBlock, blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
        Assert.That(actual.Result.Error, Does.Contain("Cannot find header"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DebugStandardTraceBlockToFile_WhenStateUnavailable_ReturnsResourceUnavailable(bool isBadBlock)
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
    public async Task DebugMigrateReceipts_WhenInvoked_ReturnsResponse()
    {
        _debugBridge.MigrateReceipts(Arg.Any<ulong>(), Arg.Any<ulong>()).Returns(true);

        string response = await SerializedRequest("debug_migrateReceipts", 100);
        Assert.That(response, Is.Not.Null);
    }

    [Test]
    public async Task DebugResetHead_WhenInvoked_UpdatesHeadBlock()
    {
        _debugBridge.UpdateHeadBlock(Arg.Any<Hash256>());

        await SerializedRequest("debug_resetHead", TestItem.KeccakA);
        _debugBridge.Received().UpdateHeadBlock(TestItem.KeccakA);
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
}
