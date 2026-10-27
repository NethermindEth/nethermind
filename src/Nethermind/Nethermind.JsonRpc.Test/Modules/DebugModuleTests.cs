// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
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
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ReturnsExtensions;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using ResultType = Nethermind.Core.ResultType;

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
    private readonly ICodeInfoRepository _codeInfoRepository = Substitute.For<ICodeInfoRepository>();

    private DebugRpcModule CreateModule() => new(
        LimboLogs.Instance,
        _debugBridge,
        _jsonRpcConfig,
        _specProvider,
        _blockchainBridge,
        new BlocksConfig(),
        _blockFinder,
        new BlockForRpcFactory(),
        _codeInfoRepository);

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
    public void DebugTraceBlockByNumber_DoesNotMapHostFormatFailureToInvalidInput()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;
        _blockFinder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).Returns(header);
        _blockFinder.FindHeader(header.ParentHash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        _blockFinder.FindBlock(header.Hash!, header.Number).Returns(new Block(header));
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        FormatException failure = new("host state format failure");
        _debugBridge.GetBlockTrace(Arg.Any<BlockParameter>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>(),
                Arg.Any<Utf8JsonWriter?>(), Arg.Any<PipeWriter?>())
            .Returns(_ => throw failure);

        FormatException actual = Assert.Throws<FormatException>(() => CreateModule().debug_traceBlockByNumber(
            new BlockParameter(1UL), new GethTraceOptions { Tracer = "noopTracer", StreamMode = false }))!;

        Assert.That(actual, Is.SameAs(failure));
    }

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
    public async Task DebugGetRawHeader_WhenHeaderExists_ReturnsHeaderRlpWithoutLoadingBlock()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(0).TestObject;
        _blockFinder.FindHeader(new BlockParameter(0UL)).Returns(header);

        using JsonRpcResponse response = await Request("debug_getRawHeader", "0x0");
        Assert.That(RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan(), Is.SequenceEqualTo(new HeaderDecoder().Encode(header).Bytes));
        _debugBridge.DidNotReceiveWithAnyArgs().GetBlock(null!);
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
    public async Task DebugGetRawTransaction_WhenTransactionNotFound_ReturnsEmptyBytes()
    {
        _debugBridge.GetTransactionFromHash(Keccak.Zero).ReturnsNull();

        string serialized = await SerializedRequest("debug_getRawTransaction", Keccak.Zero);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x\",\"id\":67}"));
    }

    [Test]
    public async Task DebugGetRawReceipts_WhenReceiptsExist_ReturnsHexArray([Values] bool byHash)
    {
        TxReceipt[] receipts = [Build.A.Receipt.TestObject, Build.A.Receipt.TestObject];
        Block block = BlockAtNumberOne();
        _debugBridge.GetReceiptsForBlock(block).Returns(receipts);
        RlpBehaviors behavior = (_specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        string expected = $"[\"{Rlp.Encode(receipts[0], behavior).Bytes.ToHexString(true)}\",\"{Rlp.Encode(receipts[1], behavior).Bytes.ToHexString(true)}\"]";

        // Only the plain hash lookup is stubbed, so honouring requireCanonical would find nothing; geth ignores it here.
        object parameter = byHash ? new { blockHash = block.Hash, requireCanonical = true } : "0x1";
        string serialized = await SerializedRequest("debug_getRawReceipts", parameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expected},\"id\":67}}"));
    }

    [Test]
    public async Task DebugGetRawReceipts_WhenNoReceipts_ReturnsEmptyArray()
    {
        _debugBridge.GetReceiptsForBlock(BlockAtNumberOne()).Returns([]);

        string serialized = await SerializedRequest("debug_getRawReceipts", "0x1");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    // Matches geth: an unknown number has no block (null), while a hash is read directly and has no receipts ([]).
    [TestCase(false, "null")]
    [TestCase(true, "[]")]
    public async Task DebugGetRawReceipts_WhenBlockUnknown_ReturnsGethResult(bool byHash, string expectedResult)
    {
        string serialized = await SerializedRequest("debug_getRawReceipts", byHash ? TestItem.KeccakA : "0x1");

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}"));
        _debugBridge.DidNotReceiveWithAnyArgs().GetReceiptsForBlock(default!);
    }

    [Test]
    public async Task DebugGetRawReceipts_WhenHistoryPruned_ReturnsPrunedHistoryError([Values] bool byHash)
    {
        _blockFinder.LowestServedBlock.Returns(2UL);
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(5).TestObject);
        BlockHeader pruned = Build.A.BlockHeader.WithNumber(1).TestObject;
        _blockFinder.FindHeader(new BlockParameter(pruned.Hash!)).Returns(pruned);

        using JsonRpcResponse response = await Request("debug_getRawReceipts", byHash ? pruned.Hash! : "0x1");

        Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.PrunedHistoryUnavailable));
    }

    private Block BlockAtNumberOne()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        _blockFinder.FindBlock(new BlockParameter(1L)).Returns(block);
        _blockFinder.FindBlock(block.Hash!, BlockTreeLookupOptions.None).Returns(block);
        return block;
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
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp).TestObject;
        byte[] rawBal = [0xc0];
        _blockFinder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        _blockFinder.FindHeader(BlockParameter.Latest).Returns(header);
        _blockchainBridge.GetBlockAccessListRlp(header.Number, header.Hash!).Returns(ArrayMemoryManager.From(rawBal));

        string serialized = await SerializedRequest("debug_getRawBlockAccessList", "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xc0\",\"id\":67}"));
    }

    [TestCaseSource(nameof(RawBlockAccessListErrorCases))]
    public async Task DebugGetRawBlockAccessList_WhenUnavailable_ReturnsError(Action<IBlockFinder, IBlockchainBridge> setup, object parameter, int expectedErrorCode)
    {
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(100).TestObject);
        setup(_blockFinder, _blockchainBridge);

        using JsonRpcResponse response = await Request("debug_getRawBlockAccessList", parameter);
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

    [TestCase(null, false, false)]
    [TestCase("missingTracer", false, false)]
    [TestCase(null, true, false)]
    [TestCase("noopTracer", true, false)]
    [TestCase(null, true, true)]
    public async Task Debug_traceBlockByNumber_missing_numeric_block_uses_geth_error(string? tracer, bool headerRetained, bool streaming)
    {
        const ulong number = 26236007;
        BlockParameter selector = new(number);
        _jsonRpcConfig.EnableTracingStreamMode = streaming;
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(1).TestObject);
        if (headerRetained)
        {
            BlockHeader parent = Build.A.BlockHeader.WithNumber(number - 1).TestObject;
            BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;
            _blockFinder.FindHeader(selector).Returns(header);
            _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
            _blockchainBridge.HasStateForBlock(parent).Returns(true);
            _blockchainBridge.HasStateForBlock(header).Returns(true);
            _debugBridge.GetBlockTrace(Arg.Any<BlockParameter>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(_ => throw new ArgumentNullException("block"));
        }
        string response = await SerializedRequest("debug_traceBlockByNumber", selector, new { tracer });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(-32000), response);
            Assert.That((string?)json["error"]?["message"], Is.EqualTo($"block #{number} not found"), response);
            Assert.That(json["result"], Is.Null, response);
            _debugBridge.DidNotReceiveWithAnyArgs().GetBlockTrace(default(BlockParameter)!, default, default);
        }
    }

    [Test]
    public void Debug_traceBlockByNumber_streaming_missing_body_for_tag_reports_hash()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;
        _jsonRpcConfig.EnableTracingStreamMode = true;
        _blockFinder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).Returns(header);
        _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        using ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> response = CreateModule().debug_traceBlockByNumber(BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(response.Result.Error, Is.EqualTo($"Block body not found for {header.Hash}"));
        }
    }

    [Test]
    public void Debug_traceBlockByNumber_with_body_preserves_selector_replay([Values] bool filtered, [Values] bool replayThrows)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        Block block = Build.A.Block.WithParent(parent).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        BlockParameter selector = new(block.Number);
        _blockFinder.Head.Returns(block);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).Returns(block.Header);
        _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        _blockFinder.FindBlock(block.Hash!, block.Number).Returns(block);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _debugBridge.GetBlockTrace(Arg.Any<BlockParameter>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(_ => replayThrows ? throw new ArgumentNullException("block") : new GethLikeTxTraceCollection([new GethLikeTxTrace()]));
        GethTraceOptions options = new() { StreamMode = false, TxHash = filtered ? block.Transactions[0].Hash : null };

        using ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> response = CreateModule().debug_traceBlockByNumber(selector, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Result.ResultType, Is.EqualTo(replayThrows ? ResultType.Failure : ResultType.Success), response.Result.Error);
            if (replayThrows)
                Assert.That(response.Result.Error, Is.EqualTo($"Trace is null for block {selector}"));
            _debugBridge.Received(1).GetBlockTrace(Arg.Any<BlockParameter>(), Arg.Any<CancellationToken>(),
                Arg.Is<GethTraceOptions>(actual => actual.TxHash == options.TxHash));
            _debugBridge.DidNotReceiveWithAnyArgs().GetBlockTrace(default(Block)!, default, default);
        }
    }

    [Test]
    public void Debug_traceBlockByNumber_missing_numeric_block_preserves_temporary_status([Values] bool isTemporary)
    {
        const ulong number = 26236007;
        _blockFinder.Head.Returns(Build.A.Block.WithNumber(1).TestObject);
        _debugBridge.HaveNotSyncedHeadersYet().Returns(isTemporary);

        using ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> response = CreateModule().debug_traceBlockByNumber(new BlockParameter(number));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(response.Result.Error, Is.EqualTo($"block #{number} not found"));
            Assert.That(response.IsTemporary, Is.EqualTo(isTemporary));
        }
    }

    [TestCase(false, ErrorCodes.InternalError, "Incorrect head block")]
    [TestCase(true, ErrorCodes.ResourceUnavailable, "No state available for block")]
    public void Debug_traceBlockByNumber_preserves_non_lookup_failures(bool hasHead, int errorCode, string message)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        if (hasHead)
        {
            _blockFinder.Head.Returns(block);
            _blockFinder.FindHeader(new BlockParameter(1UL)).Returns(block.Header);
            _blockFinder.HasBlock(block.Number, block.Hash!).Returns(true);
            _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        }
        using ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> response = CreateModule().debug_traceBlockByNumber(new BlockParameter(1UL));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ErrorCode, Is.EqualTo(errorCode));
            Assert.That(response.Result.Error, Does.StartWith(message));
        }
    }

    [Test]
    public void DebugStandardTraceBlockToFile_WhenStateAvailable_ReturnsFileNames([Values] bool isBadBlock, [Values] bool poststateAvailable)
    {
        Hash256 blockHash = Keccak.EmptyTreeHash;

        static IEnumerable<string> GetFileNames(Hash256 hash) =>
            new[] { $"block_{hash.ToShortString()}-0", $"block_{hash.ToShortString()}-1" };

        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithHash(blockHash).TestObject;
        _blockFinder.FindHeader(blockHash).Returns(header);
        _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        _blockchainBridge.HasStateForBlock(parent).Returns(true);
        _blockchainBridge.HasStateForBlock(header).Returns(poststateAvailable);
        if (isBadBlock)
        {
            _debugBridge.GetBadBlocks().Returns([new Block(header)]);
            _debugBridge
                .TraceBadBlockToFile(Arg.Is(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
                .Returns(static c => GetFileNames(c.ArgAt<Hash256>(0)));
        }
        else
        {
            _blockFinder.HasBlock(header.Number, blockHash).Returns(true);
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
        BlockHeader parent = Build.A.BlockHeader.WithNumber(99).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithHash(blockHash).TestObject;

        _blockFinder.FindHeader(blockHash).Returns(header);
        _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        _blockchainBridge.HasStateForBlock(parent).Returns(false);
        _blockchainBridge.HasStateForBlock(header).Returns(true);
        _blockFinder.HasBlock(header.Number, blockHash).Returns(true);
        if (isBadBlock) _debugBridge.GetBadBlocks().Returns([new Block(header)]);

        ResultWrapper<IEnumerable<string>> actual = StandardTraceToFile(CreateModule(), isBadBlock, blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
        Assert.That(actual.Result.Error, Does.Contain("No state available"));
    }

    [Test]
    public void DebugIntermediateRoots_WhenStateAvailable_ReturnsRoots([Values] bool poststateAvailable, [Values] bool bodyStored)
    {
        Hash256 blockHash = TestItem.KeccakA;
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;
        _blockFinder.FindHeader(blockHash).Returns(header);
        _blockFinder.HasBlock(header.Number, blockHash).Returns(bodyStored);
        if (!bodyStored) _blockFinder.FindBlock(blockHash, header.Number).Returns(new Block(header));
        _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        _blockchainBridge.HasStateForBlock(parent).Returns(true);
        _blockchainBridge.HasStateForBlock(header).Returns(poststateAvailable);

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
    public async Task Debug_traceBlock_missing_parent_header_rejects_before_replay(
        [Values("debug_traceBlockByHash", "debug_traceBlockByNumber", "debug_traceBlock")] string method,
        [Values] bool streaming)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        _jsonRpcConfig.EnableTracingStreamMode = streaming;
        _blockFinder.Head.Returns(block);
        _blockFinder.FindHeader(block.Hash!).Returns(block.Header);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).Returns(block.Header);
        _blockFinder.HasBlock(block.Number, block.Hash!).Returns(true);
        _blockchainBridge.HasStateForBlock(block.Header).Returns(true);
        Assert.That(_blockFinder.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None, block.Number - 1), Is.Null);
        object selector = method switch
        {
            "debug_traceBlockByHash" => block.Hash!,
            "debug_traceBlockByNumber" => new BlockParameter(1UL),
            _ => new BlockDecoder().Encode(block).Bytes
        };

        JToken json = JToken.Parse(await SerializedRequest(method, selector, new { streamMode = streaming }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.ResourceUnavailable), json.ToString());
            Assert.That((string?)json["error"]?["message"], Does.Contain("Cannot find parent header"), json.ToString());
            Assert.That(json["result"], Is.Null);
            Assert.That(_debugBridge.ReceivedCalls(), Is.Empty, "missing parent must reject before replay");
        }
    }

    [TestCaseSource(nameof(TraceBaseStateGuardErrorCases))]
    public void DebugTraceTransaction_WhenTraceBaseStateGuardRejects_ReturnsResourceUnavailable(
        Func<DebugRpcModule, BlockHeader, ResultWrapper<GethLikeTxTrace>> invoke,
        Action<BlockHeader, BlockHeader, IBlockFinder, IBlockchainBridge> setup,
        string expectedErrorSubstring)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;

        setup(header, parent, _blockFinder, _blockchainBridge);
        _debugBridge.GetTransactionBlockHash(TestItem.KeccakA).Returns(header.Hash);

        ResultWrapper<GethLikeTxTrace> actual = invoke(CreateModule(), header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(actual.Result.Error, Does.Contain(expectedErrorSubstring));
        }
    }

    [Test]
    public void DebugTraceBlock_StreamingNativeTracerWithoutTraceBaseState_FailsBeforeStreaming(
        [Values("debug_traceBlock", "debug_traceBlockByNumber", "debug_traceBlockByHash")] string method,
        [Values] bool parentHeaderMissing)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        BlockHeader header = block.Header;
        _blockFinder.Head.Returns(block);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).ReturnsForAnyArgs(header);
        _blockFinder.FindHeader(header.Hash!).Returns(header);
        _blockFinder.FindBlock(Arg.Any<BlockParameter>()).ReturnsForAnyArgs(block);
        _blockFinder.FindBlock(header.Hash!).Returns(block);
        _blockFinder.HasBlock(header.Number, header.Hash!).Returns(true);
        _blockFinder.FindHeader(header.ParentHash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parentHeaderMissing ? null : parent);
        // The RLP-decoded block of debug_traceBlock is a copy, so headers are matched by hash.
        _blockchainBridge.HasStateForBlock(Arg.Is<BlockHeader>(h => h.Hash == header.Hash)).Returns(true);
        GethTraceOptions options = new() { Tracer = NativeCallTracer.CallTracer, StreamMode = true };
        DebugRpcModule module = CreateModule();

        using ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> actual = method switch
        {
            "debug_traceBlock" => module.debug_traceBlock(Rlp.Encode(block).Bytes, options),
            "debug_traceBlockByNumber" => module.debug_traceBlockByNumber(new BlockParameter(header.Number), options),
            _ => module.debug_traceBlockByHash(header.Hash!, options),
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(actual.Result.Error, Does.Contain(parentHeaderMissing ? "Cannot find parent header" : "No state available for block"));
        }
    }

    [Test]
    public void DebugTraceTransaction_WithRetainedParentAndMissingPoststate_ReachesTracer()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).TestObject;
        _debugBridge.GetTransactionBlockHash(TestItem.KeccakA).Returns(header.Hash);
        _blockFinder.FindHeader(header.Hash!).Returns(header);
        _blockFinder.FindHeader(header.ParentHash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        _blockchainBridge.HasStateForBlock(header).Returns(false);
        _blockchainBridge.HasStateForBlock(parent).Returns(true);
        GethLikeTxTrace trace = new();
        _debugBridge.GetTransactionTrace(header.Hash!, TestItem.KeccakA, Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>()).Returns(trace);

        using ResultWrapper<GethLikeTxTrace> actual = CreateModule().debug_traceTransaction(TestItem.KeccakA,
            new GethTraceOptions { Tracer = "noopTracer" });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(actual.Data, Is.SameAs(trace));
        }
    }

    [Test]
    public void DebugTraceCall_PinsValidatedHeaderHash([Values] bool streaming, [Values] bool latest, [Values] bool withOverrides)
    {
        Block validated = Build.A.Block.WithNumber(1).TestObject;
        BlockParameter selector = latest ? BlockParameter.Latest : new BlockParameter(1UL);
        _jsonRpcConfig.EnableTracingStreamMode = streaming;
        _blockFinder.Head.Returns(validated);
        _blockFinder.FindHeader(selector).Returns(validated.Header);
        _blockchainBridge.HasStateForBlock(validated.Header).Returns(true);
        _debugBridge.GetTransactionTrace(Arg.Any<Transaction>(), Arg.Any<BlockParameter>(),
            Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>(), Arg.Any<Utf8JsonWriter?>(), Arg.Any<PipeWriter?>())
            .Returns(new GethLikeTxTrace { ReturnValue = [] });
        GethTraceOptions options = new()
        {
            StateOverrides = withOverrides ? new() { [TestItem.AddressA] = new() { Nonce = 66 } } : null
        };
        TransactionForRpc call = new LegacyTransactionForRpc { To = TestItem.AddressA };

        using ResultWrapper<GethLikeTxTrace> actual = CreateModule().debug_traceCall(call, selector, options);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Success), actual.Result.Error);
        if (streaming)
        {
            using Utf8JsonWriter writer = new(new ArrayBufferWriter<byte>());
            ((GethLikeTxTraceStreamingSingleResult)actual.Data).WriteAsJson(writer);
        }
        _debugBridge.Received(1).GetTransactionTrace(Arg.Any<Transaction>(),
            Arg.Is<BlockParameter>(p => p.BlockHash == validated.Hash), Arg.Any<CancellationToken>(),
            Arg.Is<GethTraceOptions>(o => o.StateOverrides == options.StateOverrides), Arg.Any<Utf8JsonWriter?>(), Arg.Any<PipeWriter?>());
        _blockchainBridge.Received(1).HasStateForBlock(validated.Header);
    }

    [Test]
    public void DebugTraceCallMany_PinsValidatedHeader([Values] bool streaming, [Values] bool latest, [Values] bool withOverrides)
    {
        Block validated = Build.A.Block.WithNumber(1).TestObject;
        Block replacement = Build.A.Block.WithNumber(1).WithExtraData([0x01]).TestObject;
        BlockParameter selector = latest ? BlockParameter.Latest : new BlockParameter(1UL);
        _jsonRpcConfig.EnableTracingStreamMode = streaming;
        _blockFinder.Head.Returns(validated);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).Returns(validated.Header, replacement.Header);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _debugBridge.GetTransactionTrace(Arg.Any<Transaction>(), Arg.Any<BlockParameter>(),
            Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>(), Arg.Any<Utf8JsonWriter?>(), Arg.Any<PipeWriter?>())
            .Returns(new GethLikeTxTrace { ReturnValue = [] });
        _debugBridge.GetBundleTraces(Arg.Any<TransactionBundle[]>(), Arg.Any<BlockParameter>(),
            Arg.Any<ulong?>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(Array.Empty<IEnumerable<GethLikeTxTrace>>());
        _blockchainBridge.Simulate(Arg.Any<BlockHeader>(), Arg.Any<SimulatePayload<TransactionWithSourceDetails>>(),
            Arg.Any<ISimulateBlockTracerFactory<GethLikeTxTrace>>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(new SimulateOutput<GethLikeTxTrace> { Items = [] });
        TransactionBundle[] bundles = [new()
        {
            Transactions = [new LegacyTransactionForRpc { To = TestItem.AddressA }],
            StateOverrides = withOverrides ? new() { [TestItem.AddressA] = new() { Nonce = 66 } } : null
        }];

        using ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> actual = CreateModule().debug_traceCallMany(bundles, selector);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Success), actual.Result.Error);
        if (actual.Data is GethLikeTxTraceStreamingBundleResult streamed)
        {
            using Utf8JsonWriter writer = new(new ArrayBufferWriter<byte>());
            streamed.WriteAsJson(writer);
        }
        else
        {
            _ = actual.Data.ToArray();
        }
        if (withOverrides)
        {
            _blockchainBridge.Received(1).Simulate(Arg.Is<BlockHeader>(h => h.Hash == validated.Hash),
                Arg.Any<SimulatePayload<TransactionWithSourceDetails>>(), Arg.Any<ISimulateBlockTracerFactory<GethLikeTxTrace>>(),
                Arg.Any<ulong>(), Arg.Any<CancellationToken>());
            _blockFinder.Received(1).FindHeader(Arg.Any<BlockParameter>());
        }
        else if (streaming)
        {
            _debugBridge.Received(1).GetTransactionTrace(Arg.Any<Transaction>(),
                Arg.Is<BlockParameter>(p => p.BlockHash == validated.Hash), Arg.Any<CancellationToken>(),
                Arg.Any<GethTraceOptions>(), Arg.Any<Utf8JsonWriter?>(), Arg.Any<PipeWriter?>());
        }
        else
        {
            _debugBridge.Received(1).GetBundleTraces(bundles, Arg.Is<BlockParameter>(p => p.BlockHash == validated.Hash),
                Arg.Any<ulong?>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
        }
    }

    [Test]
    public void DebugTraceBlockByNumber_PinsValidatedHeaderHash([Values] bool streaming, [Values] bool latest)
    {
        Block parent = Build.A.Block.WithNumber(0).TestObject;
        Block validated = Build.A.Block.WithNumber(1).WithParent(parent).TestObject;
        Block replacement = Build.A.Block.WithNumber(1).WithParent(parent).WithExtraData([0x01]).TestObject;
        BlockParameter selector = latest ? BlockParameter.Latest : new BlockParameter(1UL);
        _jsonRpcConfig.EnableTracingStreamMode = streaming;
        _blockFinder.Head.Returns(validated);
        _blockFinder.FindHeader(selector).Returns(validated.Header);
        _blockFinder.FindBlock(selector).Returns(replacement);
        _blockFinder.FindBlock(validated.Hash!, validated.Number).Returns(validated);
        _blockFinder.HasBlock(validated.Number, validated.Hash!).Returns(true);
        _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent.Header);
        _blockchainBridge.HasStateForBlock(parent.Header).Returns(true);
        _blockchainBridge.HasStateForBlock(validated.Header).Returns(true);
        _debugBridge.GetBlockTrace(Arg.Any<BlockParameter>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(Array.Empty<GethLikeTxTrace>());

        using ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> actual = CreateModule().debug_traceBlockByNumber(selector);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Success), actual.Result.Error);
        if (streaming)
        {
            using Utf8JsonWriter writer = new(new ArrayBufferWriter<byte>());
            ((GethLikeTxTraceStreamingBlockResult)actual.Data).WriteAsJson(writer);
            _blockFinder.Received(1).FindBlock(validated.Hash!, validated.Number);
            _debugBridge.Received(1).GetBlockTrace(Arg.Is<BlockParameter>(p => p.BlockHash == validated.Hash), Arg.Any<CancellationToken>(),
                Arg.Any<GethTraceOptions>(), writer, Arg.Any<PipeWriter?>());
        }
        else
        {
            _debugBridge.Received(1).GetBlockTrace(Arg.Is<BlockParameter>(p => p.BlockHash == validated.Hash),
                Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
        }
    }

    [Test]
    public void DebugTraceCall_PreflightOnlyUsesReadOnlyRepositoryLookup([Values] bool streaming, [Values] bool mixedFees)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        BlockParameter selector = new(block.Hash!);
        _blockFinder.Head.Returns(block);
        _jsonRpcConfig.EnableTracingStreamMode = streaming;
        _blockFinder.FindHeader(selector).Returns(block.Header);
        _blockchainBridge.HasStateForBlock(block.Header).Returns(true);
        _codeInfoRepository.GetPrecompile(TestItem.AddressA, Arg.Any<IReleaseSpec>()).Returns(IdentityPrecompile.Instance);
        _codeInfoRepository.ClearReceivedCalls();
        GethTraceOptions options = new()
        {
            StateOverrides = new()
            {
                [TestItem.AddressA] = new() { MovePrecompileToAddress = TestItem.AddressB },
                [TestItem.AddressB] = new()
            }
        };
        TransactionForRpc call = new EIP1559TransactionForRpc
        {
            To = TestItem.AddressC,
            GasPrice = 1,
            MaxFeePerGas = mixedFees ? 2 : null
        };

        using ResultWrapper<GethLikeTxTrace> actual = CreateModule().debug_traceCall(call, selector, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(actual.Result.Error, Is.EqualTo($"account {TestItem.AddressB} is already overridden"));
            _codeInfoRepository.Received(1).GetPrecompile(TestItem.AddressA, Arg.Any<IReleaseSpec>());
            foreach (ICall received in _codeInfoRepository.ReceivedCalls())
                Assert.That(received.GetMethodInfo().Name, Is.EqualTo(nameof(ICodeInfoRepository.GetPrecompile)),
                    "preflight must not read account code, reset/move overrides or mutate the repository");
            _debugBridge.DidNotReceiveWithAnyArgs().GetTransactionTrace(default(Transaction)!, default!, default, default, default, default);
        }
    }

    [Test]
    public void DebugDiagnosticReplay_ForBadStoreBlock_UsesRetainedParent([Values] bool intermediateRoots, [Values] bool headerRetained)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(parent).WithNumber(2).TestObject).TestObject;
        _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        if (headerRetained) _blockFinder.FindHeader(block.Hash!).Returns(block.Header);
        _blockchainBridge.HasStateForBlock(parent).Returns(true);
        _debugBridge.GetBadBlocks().Returns([block]);
        _debugBridge.GetBlockIntermediateRoots(block.Hash!, Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(new[] { TestItem.KeccakA });
        _debugBridge.TraceBadBlockToFile(block.Hash!, Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(new[] { "trace.jsonl" });

        DebugRpcModule module = CreateModule();
        ResultType actual = intermediateRoots
            ? module.debug_intermediateRoots(block.Hash!).Result.ResultType
            : module.debug_standardTraceBadBlockToFile(block.Hash!).Result.ResultType;

        Assert.That(actual, Is.EqualTo(ResultType.Success));
        _blockchainBridge.Received(1).HasStateForBlock(parent);
        _blockchainBridge.DidNotReceive().HasStateForBlock(block.Header);
        _debugBridge.Received(1).GetBadBlocks();
    }

    [Test]
    public void DebugDiagnosticReplay_WhenParentUnavailable_DoesNotTrace(
        [Values("roots", "file", "badFile", "badTrace")] string method, [Values] bool parentHeaderAvailable)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithNumber(2).TestObject;
        _blockFinder.FindHeader(header.Hash!).Returns(header);
        _blockFinder.HasBlock(header.Number, header.Hash!).Returns(true);
        if (parentHeaderAvailable)
            _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number).Returns(parent);
        _blockchainBridge.HasStateForBlock(header).Returns(true);
        _debugBridge.GetBadBlocks().Returns([new Block(header)]);
        DebugRpcModule module = CreateModule();

        IResultWrapper actual = method switch
        {
            "roots" => module.debug_intermediateRoots(header.Hash!),
            "badTrace" => module.debug_traceBadBlock(header.Hash!),
            _ => StandardTraceToFile(module, method == "badFile", header.Hash!)
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(actual.Result.Error, Does.Contain(parentHeaderAvailable ? "No state available" : "Cannot find parent header"));
        }
        _debugBridge.DidNotReceive().GetBlockIntermediateRoots(Arg.Any<Hash256>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
        _debugBridge.DidNotReceive().TraceBlockToFile(Arg.Any<Hash256>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
        _debugBridge.DidNotReceive().TraceBadBlockToFile(Arg.Any<Hash256>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
        _debugBridge.DidNotReceive().GetBlockTrace(Arg.Any<Block>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>(),
            Arg.Any<Utf8JsonWriter?>(), Arg.Any<PipeWriter?>());
    }

    [Test]
    public void DebugStandardTraceBlockToFile_WhenTreeBodyMissing_DoesNotUseBadStore()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        _blockFinder.FindHeader(block.Hash!).Returns(block.Header);
        _debugBridge.GetBadBlocks().Returns([block]);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        using ResultWrapper<IEnumerable<string>> actual = CreateModule().debug_standardTraceBlockToFile(block.Hash!);

        Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
        _debugBridge.DidNotReceive().GetBadBlocks();
        _debugBridge.DidNotReceive().TraceBlockToFile(Arg.Any<Hash256>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
    }

    [Test]
    public void DebugTraceBadBlock_WhenNotInBadStore_ReturnsGethNotFoundError([Values] bool inTree)
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        if (inTree)
        {
            _blockFinder.FindHeader(block.Hash!).Returns(block.Header);
            _blockFinder.FindBlock(block.Hash!).Returns(block);
        }

        ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> actual = CreateModule().debug_traceBadBlock(block.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(actual.Result.Error, Is.EqualTo($"bad block {block.Hash} not found"));
        }
    }

    [Test]
    public void DebugTraceBadBlock_ForGenesis_ReturnsNotTraceable()
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        _debugBridge.GetBadBlocks().Returns([genesis]);

        ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> actual = CreateModule().debug_traceBadBlock(genesis.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(actual.Result.Error, Is.EqualTo("genesis is not traceable"));
        }
    }

    [Test]
    public void DebugStandardTraceBadBlockToFile_WhenOnlyTreeHeaderExists_ReturnsResourceNotFound()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;
        _blockFinder.FindHeader(header.Hash!).Returns(header);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        using ResultWrapper<IEnumerable<string>> actual = CreateModule().debug_standardTraceBadBlockToFile(header.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
        }
        _debugBridge.DidNotReceive().TraceBadBlockToFile(Arg.Any<Hash256>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
    }

    [Test]
    public void DebugTraceBlockByNumber_NativeStreamTimeout_preserves_hash_and_prerequisites(
        [Values("available", "body", "parent", "state")] string availability)
    {
        Block parent = Build.A.Block.WithNumber(0).TestObject;
        Block validated = Build.A.Block.WithNumber(1).WithParent(parent).TestObject;
        _blockFinder.Head.Returns(validated);
        _blockFinder.FindHeader(BlockParameter.Latest).Returns(validated.Header);
        _blockFinder.FindHeader(parent.Hash!, BlockTreeLookupOptions.None, parent.Number)
            .Returns(availability == "parent" ? null : parent.Header);
        _blockFinder.FindBlock(validated.Hash!, validated.Number).Returns(availability == "body" ? null : validated);
        _blockchainBridge.HasStateForBlock(validated.Header).Returns(true);
        _blockchainBridge.HasStateForBlock(parent.Header).Returns(availability != "state");
        GethTraceOptions options = new() { Tracer = "callTracer", StreamMode = true, Timeout = TimeSpan.FromSeconds(5) };

        using ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> actual = CreateModule().debug_traceBlockByNumber(BlockParameter.Latest, options);

        if (availability != "available")
        {
            Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(actual.ErrorCode, Is.EqualTo(availability == "body" ? ErrorCodes.ResourceNotFound : ErrorCodes.ResourceUnavailable));
            Assert.That(_debugBridge.ReceivedCalls(), Is.Empty);
            return;
        }

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(actual.Data, Is.InstanceOf<GethLikeTxTraceStreamingBlockResult>());
        using Utf8JsonWriter writer = new(new ArrayBufferWriter<byte>());
        ((GethLikeTxTraceStreamingBlockResult)actual.Data).WriteAsJson(writer);
        _debugBridge.Received(1).GetBlockTrace(Arg.Is<BlockParameter>(p => p.BlockHash == validated.Hash),
            Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>(), writer, Arg.Any<PipeWriter?>());
        _debugBridge.DidNotReceive().GetBlockTrace(Arg.Any<Block>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>(),
            Arg.Any<Utf8JsonWriter?>(), Arg.Any<PipeWriter?>());
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
    public async Task DebugGetBadBlocks_WhenFileOmittedOrNull_ReturnsList([Values] bool explicitNull, [Values(0, 1)] int count)
    {
        Block[] blocks = Enumerable.Range(1, count).Select(static n => Build.A.Block.WithNumber(n).TestObject).ToArray();
        _debugBridge.GetBadBlocks().Returns(blocks);

        string response = explicitNull
            ? await SerializedRequest("debug_getBadBlocks", new object?[] { null })
            : await SerializedRequest("debug_getBadBlocks");

        JToken? result = JToken.Parse(response)["result"];
        Assert.That(result?.Select(static b => (string?)b["hash"]), Is.EqualTo(blocks.Select(static b => b.Hash!.ToString())));
    }

    [Test]
    public async Task DebugGetBadBlocks_returns_block_access_list_when_present([Values] bool hasBal)
    {
        byte[] bal = [0xc1, 0xc0];
        _debugBridge.GetBadBlocks().Returns([Build.A.Block.WithNumber(1).WithEncodedBlockAccessList(hasBal ? bal : null).TestObject]);

        JToken result = JToken.Parse(await SerializedRequest("debug_getBadBlocks"))["result"]![0]!;

        Assert.That((string?)result["blockAccessList"], Is.EqualTo(hasBal ? bal.ToHexString(true) : null));
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenFileGiven_WritesListAndReturnsNull()
    {
        _debugBridge.GetBadBlocks().Returns([Build.A.Block.WithNumber(1).TestObject]);
        using TempPath file = TempPath.GetTempFile();

        JToken listResult = JToken.Parse(await SerializedRequest("debug_getBadBlocks"))["result"]!;
        JToken fileResult = JToken.Parse(await SerializedRequest("debug_getBadBlocks", file.Path))["result"]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fileResult.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(JToken.DeepEquals(JToken.Parse(await File.ReadAllTextAsync(file.Path)), listResult), Is.True);
        }
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenFileExists_ReturnsErrorAndKeepsFile()
    {
        _debugBridge.GetBadBlocks().Returns([]);
        using TempPath file = TempPath.GetTempFile();
        await File.WriteAllTextAsync(file.Path, "existing");

        using JsonRpcResponse response = await Request("debug_getBadBlocks", file.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertError(response).Message, Is.EqualTo("location would overwrite an existing file"));
            Assert.That(await File.ReadAllTextAsync(file.Path), Is.EqualTo("existing"));
        }
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenDirectoryMissing_ReturnsError()
    {
        _debugBridge.GetBadBlocks().Returns([]);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "bad-blocks.json");

        using JsonRpcResponse response = await Request("debug_getBadBlocks", path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.Default));
            Assert.That(File.Exists(path), Is.False);
        }
    }

    private static IEnumerable<TestCaseData> NonAbsoluteFileArguments()
    {
        yield return new TestCaseData(123, "123");
        yield return new TestCaseData(true, "true");
        yield return new TestCaseData(new Dictionary<string, string>(), "{}");
        yield return new TestCaseData("bad-blocks.json", "bad-blocks.json");
    }

    [TestCaseSource(nameof(NonAbsoluteFileArguments))]
    public async Task DebugGetBadBlocks_WhenFileNotAbsolute_ReturnsInvalidParamsAndWritesNothing(object argument, string fileName)
    {
        _debugBridge.GetBadBlocks().Returns([]);

        using JsonRpcResponse response = await Request("debug_getBadBlocks", argument);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(File.Exists(fileName), Is.False);
        }
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenFileIsNetworkOrDevicePath_ReturnsInvalidParamsAndWritesNothing([Values("//", @"\\?\")] string prefix)
    {
        _debugBridge.GetBadBlocks().Returns([]);
        using TempPath file = TempPath.GetTempFile();

        // "//" + an absolute path is a UNC path on Windows and the same local file on Unix; "\\?\" is a Windows device path.
        using JsonRpcResponse response = await Request("debug_getBadBlocks", prefix + file.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(File.Exists(file.Path), Is.False);
        }
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenPathIsDirectory_ReturnsExistingFileError()
    {
        _debugBridge.GetBadBlocks().Returns([]);
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);

        using JsonRpcResponse response = await Request("debug_getBadBlocks", directory.Path);

        Assert.That(RpcTest.AssertError(response).Message, Is.EqualTo("location would overwrite an existing file"));
    }

    [Test]
    public async Task DebugGetBadBlocks_WhileWriting_TargetHoldsNoPartialFile()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string file = Path.Combine(directory.Path, "bad-blocks.json");
        Block block = Build.A.Block.WithNumber(1).TestObject;
        bool targetExistedDuringWrite = true;
        _debugBridge.GetBadBlocks().Returns(CheckTargetWhileWriting());

        using JsonRpcResponse response = await Request("debug_getBadBlocks", file);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertSuccess<IEnumerable<BadBlock>>(response), Is.Null);
            Assert.That(targetExistedDuringWrite, Is.False, "readers must not see a partial file at the target path");
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Is.EqualTo(new[] { file }));
            Assert.That(JToken.Parse(await File.ReadAllTextAsync(file)).Select(static b => (string?)b["hash"]), Is.EqualTo(new[] { block.Hash!.ToString() }));
        }

        IEnumerable<Block> CheckTargetWhileWriting()
        {
            yield return block;
            targetExistedDuringWrite = File.Exists(file);
        }
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenWriteFails_LeavesNoFileSoRetrySucceeds()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string file = Path.Combine(directory.Path, "bad-blocks.json");
        Block block = Build.A.Block.WithNumber(1).TestObject;
        _debugBridge.GetBadBlocks().Returns(FailAfter(block), [block]);

        using JsonRpcResponse failed = await Request("debug_getBadBlocks", file);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertError(failed).Code, Is.EqualTo(ErrorCodes.Default));
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Is.Empty, "neither the target nor a temporary file is left behind");
        }

        using JsonRpcResponse retried = await Request("debug_getBadBlocks", file);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertSuccess<IEnumerable<BadBlock>>(retried), Is.Null);
            Assert.That(JToken.Parse(await File.ReadAllTextAsync(file)).Select(static b => (string?)b["hash"]), Is.EqualTo(new[] { block.Hash!.ToString() }));
        }

        static IEnumerable<Block> FailAfter(Block block)
        {
            yield return block;
            throw new IOException("No space left on device");
        }
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenFileCreatedByOthersDuringFailedWrite_KeepsIt()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string file = Path.Combine(directory.Path, "bad-blocks.json");
        _debugBridge.GetBadBlocks().Returns(FailAfterOtherWriterCreatesFile());

        using JsonRpcResponse response = await Request("debug_getBadBlocks", file);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertError(response).Message, Is.EqualTo("original write error"));
            Assert.That(File.Exists(file) ? await File.ReadAllTextAsync(file) : null, Is.EqualTo("other writer"), "a failed write must only remove what it created");
        }

        IEnumerable<Block> FailAfterOtherWriterCreatesFile()
        {
            yield return Build.A.Block.WithNumber(1).TestObject;
            if (File.Exists(file)) File.Delete(file); // Unix allows unlinking a file that is still open for writing.
            File.WriteAllText(file, "other writer");
            throw new IOException("original write error");
        }
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenFileCreatedByOthersDuringWrite_ReturnsExistingFileErrorAndKeepsIt()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string file = Path.Combine(directory.Path, "bad-blocks.json");
        _debugBridge.GetBadBlocks().Returns(OtherWriterCreatesFile());

        using JsonRpcResponse response = await Request("debug_getBadBlocks", file);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertError(response).Message, Is.EqualTo("location would overwrite an existing file"));
            Assert.That(Directory.GetFileSystemEntries(directory.Path), Is.EqualTo(new[] { file }), "the temporary file is removed");
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("other writer"));
        }

        IEnumerable<Block> OtherWriterCreatesFile()
        {
            yield return Build.A.Block.WithNumber(1).TestObject;
            File.WriteAllText(file, "other writer");
        }
    }

    [Test]
    public async Task DebugGetBadBlocks_WhenExtraParamGiven_ReturnsInvalidParams()
    {
        _debugBridge.GetBadBlocks().Returns([]);

        using JsonRpcResponse response = await Request("debug_getBadBlocks", null, 1);

        Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [Test]
    [Platform("Linux,MacOsX")]
    public async Task DebugGetBadBlocks_WhenTemporaryFileCleanupFails_PreservesWriteError()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string file = Path.Combine(directory.Path, "bad-blocks.json");
        _debugBridge.GetBadBlocks().Returns(FailAfterReplacingTemporaryFile());

        using JsonRpcResponse response = await Request("debug_getBadBlocks", file);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.Default));
            Assert.That(RpcTest.AssertError(response).Message, Is.EqualTo("original write error"));
        }

        IEnumerable<Block> FailAfterReplacingTemporaryFile()
        {
            yield return Build.A.Block.WithNumber(1).TestObject;
            // Unix allows unlinking the open output; a directory at that path makes cleanup fail.
            string temporary = Directory.GetFiles(directory.Path).Single();
            File.Delete(temporary);
            Directory.CreateDirectory(temporary);
            throw new IOException("original write error");
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
    public void DebugTraceTransactionInBlockByIndex_WithCustomTracer_TracesOnlyTheTarget()
    {
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _blockFinder.FindHeader(Arg.Any<Hash256>(), BlockTreeLookupOptions.None, 0)
            .Returns(Build.A.Block.WithNumber(0).TestObject.Header);
        GethLikeTxTrace trace = new();
        Block? tracedBlock = null;
        Hash256? tracedHash = null;
        _debugBridge
            .GetTransactionTrace(Arg.Do<Block>(b => tracedBlock = b), Arg.Do<Hash256>(h => tracedHash = h), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(trace);

        // A custom Tracer forces the non-streaming path (CanStreamStructLogs returns false when it's set).
        GethTraceOptions options = new() { Tracer = "callTracer" };

        using ResultWrapper<GethLikeTxTrace> result = CreateModule().debug_traceTransactionInBlockByIndex(BlockRlpFixture(3), 1, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data, Is.SameAs(trace));
            Assert.That(tracedHash, Is.EqualTo(tracedBlock!.Transactions[1].Hash));
            _debugBridge.DidNotReceive().GetBlockTrace(Arg.Any<Block>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
        }
    }

    [Test]
    public void DebugTraceTransactionInBlockByIndex_WithCustomTracer_WhenIndexOutOfRange_FailsWithoutTracing([Values(-1, 5)] int txIndex)
    {
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _blockFinder.FindHeader(Arg.Any<Hash256>(), BlockTreeLookupOptions.None, 0)
            .Returns(Build.A.Block.WithNumber(0).TestObject.Header);
        GethTraceOptions options = new() { Tracer = "callTracer" };

        using ResultWrapper<GethLikeTxTrace> result = CreateModule().debug_traceTransactionInBlockByIndex(BlockRlpFixture(2), txIndex, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            _debugBridge.DidNotReceive().GetTransactionTrace(Arg.Any<Block>(), Arg.Any<Hash256>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
            _debugBridge.DidNotReceive().GetBlockTrace(Arg.Any<Block>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>());
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
        yield return new TestCaseData(
            (Action<IDebugBridge>)(_ => { }),
            "debug_getRawHeader", (object)"0x1")
        { TestName = "RawHeader_ByNumber" };
    }

    private static IEnumerable<TestCaseData> RawBlockAccessListErrorCases()
    {
        yield return new TestCaseData(
            (Action<IBlockFinder, IBlockchainBridge>)((_, _) => { }),
            (object)"latest", ErrorCodes.BlockAccessListResourceNotFound)
        { TestName = "missing_block" };

        yield return new TestCaseData(
            (Action<IBlockFinder, IBlockchainBridge>)((_, _) => { }),
            (object)"finalized", ErrorCodes.BlockAccessListResourceNotFound)
        { TestName = "unknown_finalized_block" };

        // Pruning history keeps the header, so a pre-Amsterdam block below the served floor is still known.
        foreach (bool isHistoryExpired in new[] { false, true })
        {
            yield return new TestCaseData(
                (Action<IBlockFinder, IBlockchainBridge>)((finder, _) =>
                {
                    finder.FindHeader(new BlockParameter(1UL)).Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
                    if (isHistoryExpired) finder.LowestServedBlock.Returns(10UL);
                }),
                (object)"0x1", ErrorCodes.BlockAccessListResourceNotFound)
            { TestName = isHistoryExpired ? "history_expired_before_fork" : "unavailable_before_fork" };

            yield return new TestCaseData(
                (Action<IBlockFinder, IBlockchainBridge>)((finder, chain) =>
                {
                    BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp).TestObject;
                    finder.FindHeader(new BlockParameter(1UL)).Returns(header);
                    if (isHistoryExpired) finder.LowestServedBlock.Returns(10UL);
                    chain.GetBlockAccessListRlp(header.Number, header.Hash!).ReturnsNull();
                }),
                (object)"0x1", ErrorCodes.PrunedHistoryUnavailable)
            { TestName = isHistoryExpired ? "history_expired" : "pruned" };
        }

        BlockHeader sideHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
        yield return new TestCaseData(
            (Action<IBlockFinder, IBlockchainBridge>)((finder, _) => finder.FindHeader(sideHeader.Hash!).Returns(sideHeader)),
            (object)new BlockParameter(sideHeader.Hash!, requireCanonical: true), ErrorCodes.InvalidInput)
        { TestName = "non_canonical_require_canonical" };
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
                finder.HasBlock(header.Number, blockHash).Returns(true);
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
            ),
            (
                "ByTransactionHash",
                static (module, _) => module.debug_traceTransaction(TestItem.KeccakA, new GethTraceOptions { Tracer = "noopTracer" }),
                static (header, finder) => finder.FindHeader(header.Hash!).Returns(header)
            ),
            (
                "ByTransactionHashStreaming",
                static (module, _) => module.debug_traceTransaction(TestItem.KeccakA),
                static (header, finder) => finder.FindHeader(header.Hash!).Returns(header)
            ),
            (
                "ByTransactionHashMalformedTimeout",
                static (module, _) => module.debug_traceTransaction(TestItem.KeccakA, new GethTraceOptions
                {
                    Tracer = "noopTracer", ParsedTimeout = CustomTimeDurationConverter.Parse("bad")
                }),
                static (header, finder) => finder.FindHeader(header.Hash!).Returns(header)
            ),
            (
                "ByTransactionHashStreamingMalformedTimeout",
                static (module, _) => module.debug_traceTransaction(TestItem.KeccakA, new GethTraceOptions
                {
                    ParsedTimeout = CustomTimeDurationConverter.Parse("bad")
                }),
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
