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

// Tests with mocked IDebugBridge
[Parallelizable(ParallelScope.Self)]
public class DebugModuleTests
{
    private readonly IJsonRpcConfig _jsonRpcConfig = new JsonRpcConfig();
    private readonly ISpecProvider _specProvider = SpecProviderSubstitute.Create();
    private readonly IDebugBridge _debugBridge = Substitute.For<IDebugBridge>();
    private readonly IBlockFinder _blockFinder = Substitute.For<IBlockFinder>();
    private readonly IBlockchainBridge _blockchainBridge = Substitute.For<IBlockchainBridge>();
    private readonly MemDb _blocksDb = new();

    private DebugRpcModule CreateDebugRpcModule(IDebugBridge customDebugBridge) => new(
            LimboLogs.Instance,
            customDebugBridge,
            _jsonRpcConfig,
            _specProvider,
            _blockchainBridge,
            new BlocksConfig(),
            _blockFinder
        );

    [Test]
    public void Debug_traceCallMany_streams_under_live_cancellation_token()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;
        _blockFinder.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        _blockFinder.FindHeader(Arg.Any<BlockParameter>()).ReturnsForAnyArgs(header);
        _blockchainBridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _debugBridge
            .GetBundleTraces(Arg.Any<TransactionBundle[]>(), Arg.Any<BlockParameter>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(static c => StreamBundles(c.ArgAt<CancellationToken>(3)));

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
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

    [Test]
    public async Task Get_from_db()
    {
        byte[] key = [1, 2, 3];
        byte[] value = [4, 5, 6];
        _debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(value);
        _ = Substitute.For<IConfigProvider>();
        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        using JsonRpcResponse response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getFromDb", "STATE", key);

        RpcTest.AssertSuccess<byte[]>(response);
    }

    [Test]
    public async Task Get_from_db_null_value()
    {
        _debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns((byte[])null!);
        _ = Substitute.For<IConfigProvider>();
        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        byte[] key = [1, 2, 3];
        using JsonRpcResponse response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getFromDb", "STATE", key);

        RpcTest.AssertSuccess<byte[]>(response);
    }

    [TestCase(1)]
    [TestCase(0x1)]
    public async Task Get_chain_level(object parameter)
    {
        _debugBridge.GetLevelInfo(1).Returns(new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1000), new BlockInfo(TestItem.KeccakB, 1001)));

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        using JsonRpcResponse response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getChainLevel", parameter);
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
        IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
        Block block = Build.A.Block.WithNumber(0).TestObject;
        Rlp expected = new HeaderDecoder().Encode(block.Header);
        debugBridge.GetBlock(new BlockParameter(0L)).Returns(block);

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using JsonRpcResponse response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getRawHeader", "0x0");
        Assert.That(RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan().ToArray(), Is.EqualTo(expected.Bytes));
    }

    private static IEnumerable<TestCaseData> RawBlockCases()
    {
        yield return new TestCaseData("0x1", new BlockParameter(1L)) { TestName = "ByNumber" };
        yield return new TestCaseData(Keccak.Zero, new BlockParameter(Keccak.Zero)) { TestName = "ByHash" };
        yield return new TestCaseData("latest", BlockParameter.Latest) { TestName = "ByName" };
    }

    [TestCaseSource(nameof(RawBlockCases))]
    public async Task DebugGetRawBlock_WhenBlockExists_ReturnsBlockRlp(object requestParameter, BlockParameter blockParameter)
    {
        IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
        Block block = Build.A.Block.WithNumber(1).TestObject;
        Rlp expected = new BlockDecoder().Encode(block);
        debugBridge.GetBlock(blockParameter).Returns(block);

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using JsonRpcResponse response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getRawBlock", requestParameter);
        Assert.That(RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan().ToArray(), Is.EqualTo(expected.Bytes));
    }

    private static IEnumerable<TestCaseData> RawBlockMissingCases()
    {
        yield return new TestCaseData("0x1", new BlockParameter(1L)) { TestName = "ByNumber" };
        yield return new TestCaseData(Keccak.Zero, new BlockParameter(Keccak.Zero)) { TestName = "ByHash" };
    }

    [TestCaseSource(nameof(RawBlockMissingCases))]
    public async Task DebugGetRawBlock_WhenBlockMissing_ReturnsResourceNotFound(object requestParameter, BlockParameter blockParameter)
    {
        IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
        debugBridge.GetBlock(blockParameter).ReturnsNull();

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using JsonRpcResponse response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getRawBlock", requestParameter);
        Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.ResourceNotFound));
    }

    [Test]
    public async Task DebugGetRawTransaction_WhenTransactionExists_ReturnsTransactionRlp()
    {
        IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        string expected = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true);
        debugBridge.GetTransactionFromHash(transaction.Hash!).Returns(transaction);

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        string serialized = await RpcTest.TestSerializedRequest<IDebugRpcModule>(rpcModule, "debug_getRawTransaction", transaction.Hash!);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expected}\",\"id\":67}}"));
    }

    [Test]
    public async Task DebugGetRawTransaction_WhenTransactionMissing_ReturnsResourceNotFound()
    {
        IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
        debugBridge.GetTransactionFromHash(Keccak.Zero).ReturnsNull();

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using JsonRpcResponse response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getRawTransaction", Keccak.Zero);
        Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(ErrorCodes.ResourceNotFound));
    }

    [Test]
    public async Task DebugGetRawReceipts_WhenReceiptsExist_ReturnsHexArray()
    {
        IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
        TxReceipt[] receipts = [Build.A.Receipt.TestObject, Build.A.Receipt.TestObject];
        debugBridge.GetReceiptsForBlock(new BlockParameter(1L)).Returns(receipts);
        RlpBehaviors behavior = (_specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        string expected = $"[\"{Rlp.Encode(receipts[0], behavior).Bytes.ToHexString(true)}\",\"{Rlp.Encode(receipts[1], behavior).Bytes.ToHexString(true)}\"]";

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        string serialized = await RpcTest.TestSerializedRequest<IDebugRpcModule>(rpcModule, "debug_getRawReceipts", "0x1");
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expected},\"id\":67}}"));
    }

    [Test]
    public async Task DebugGetRawReceipts_WhenNoReceipts_ReturnsEmptyArray()
    {
        IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
        debugBridge.GetReceiptsForBlock(new BlockParameter(1L)).Returns([]);

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        string serialized = await RpcTest.TestSerializedRequest<IDebugRpcModule>(rpcModule, "debug_getRawReceipts", "0x1");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    [Test]
    public async Task Get_raw_block_access_list()
    {
        Block block = Build.A.Block.WithNumber(1).WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp).TestObject;
        byte[] rawBal = [0xc0];
        _debugBridge.GetBlock(BlockParameter.Latest).Returns(block);
        _blockchainBridge.GetBlockAccessListRlp(block.Number, block.Hash!).Returns(ArrayMemoryManager.From(rawBal));

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        string serialized = await RpcTest.TestSerializedRequest<IDebugRpcModule>(rpcModule, "debug_getRawBlockAccessList", "latest");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xc0\",\"id\":67}"));
    }

    private static IEnumerable<TestCaseData> GetRawBlockAccessListErrorCases()
    {
        yield return new TestCaseData(
            (Action<IDebugBridge, IBlockchainBridge>)((debug, _) => debug.GetBlock(BlockParameter.Latest).ReturnsNull()),
            ErrorCodes.BlockAccessListResourceNotFound)
        { TestName = "Get_raw_block_access_list_when_missing_block" };

        yield return new TestCaseData(
            (Action<IDebugBridge, IBlockchainBridge>)((debug, _) =>
                debug.GetBlock(BlockParameter.Latest).Returns(Build.A.Block.WithNumber(1).TestObject)),
            ErrorCodes.BlockAccessListResourceNotFound)
        { TestName = "Get_raw_block_access_list_when_unavailable_before_fork" };

        yield return new TestCaseData(
            (Action<IDebugBridge, IBlockchainBridge>)((debug, chain) =>
            {
                Block block = Build.A.Block.WithNumber(1).WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp).TestObject;
                debug.GetBlock(BlockParameter.Latest).Returns(block);
                chain.GetBlockAccessListRlp(block.Number, block.Hash!).ReturnsNull();
            }),
            ErrorCodes.PrunedHistoryUnavailable)
        { TestName = "Get_raw_block_access_list_when_pruned" };
    }

    [TestCaseSource(nameof(GetRawBlockAccessListErrorCases))]
    public async Task Get_raw_block_access_list_error_cases(Action<IDebugBridge, IBlockchainBridge> setup, int expectedErrorCode)
    {
        setup(_debugBridge, _blockchainBridge);

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        using JsonRpcResponse response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getRawBlockAccessList", "latest");

        Assert.That(RpcTest.AssertError(response).Code, Is.EqualTo(expectedErrorCode));
    }

    private BlockTree BuildBlockTree(Func<BlockTreeBuilder, BlockTreeBuilder>? builderOptions = null)
    {
        BlockTreeBuilder builder = Build.A.BlockTree().WithBlocksDb(_blocksDb).WithBlockStore(new BlockStore(_blocksDb));
        builder = builderOptions?.Invoke(builder) ?? builder;
        return builder.TestObject;
    }

    [Test]
    public void Debug_getBadBlocks_test()
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

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        ResultWrapper<IEnumerable<BadBlock>> blocks = rpcModule.debug_getBadBlocks();
        Assert.That(blocks.Data.Count(), Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Data.ElementAt(0).Hash, Is.EqualTo(block1.Hash));
            Assert.That(blocks.Data.ElementAt(0).Block.Difficulty, Is.EqualTo(new UInt256(2)));
        }
    }

    [Test]
    public void Debug_traceCall_test()
    {
        GethTxTraceEntry entry = new()
        {
            Storage = new Dictionary<string, string>
            {
                {"1".PadLeft(64, '0'), "2".PadLeft(64, '0')},
                {"3".PadLeft(64, '0'), "4".PadLeft(64, '0')},
            },
            Memory =
            [
                "5".PadLeft(64, '0'),
                "6".PadLeft(64, '0')
            ],
            Stack = [],
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

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
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
                        Memory =
                        [
                            "0000000000000000000000000000000000000000000000000000000000000005",
                            "0000000000000000000000000000000000000000000000000000000000000006"
                        ],
                        Opcode = "STOP",
                        ProgramCounter = 0,
                        Stack = [],
                        Storage = new Dictionary<string, string>
                        {
                            {
                                "0000000000000000000000000000000000000000000000000000000000000001",
                                "0000000000000000000000000000000000000000000000000000000000000002"
                            },
                            {
                                "0000000000000000000000000000000000000000000000000000000000000003",
                                "0000000000000000000000000000000000000000000000000000000000000004"
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
    public async Task Migrate_receipts()
    {
        _debugBridge.MigrateReceipts(Arg.Any<long>(), Arg.Any<long>()).Returns(true);
        IDebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        string response = await RpcTest.TestSerializedRequest(rpcModule, "debug_migrateReceipts", 100);
        Assert.That(response, Is.Not.Null);
    }

    [Test]
    public async Task Update_head_block()
    {
        _debugBridge.UpdateHeadBlock(Arg.Any<Hash256>());
        IDebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        await RpcTest.TestSerializedRequest(rpcModule, "debug_resetHead", TestItem.KeccakA);
        _debugBridge.Received().UpdateHeadBlock(TestItem.KeccakA);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StandardTraceBlockToFile(bool isBadBlock)
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

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        ResultWrapper<IEnumerable<string>> actual = isBadBlock
            ? rpcModule.debug_standardTraceBadBlockToFile(blockHash)
            : rpcModule.debug_standardTraceBlockToFile(blockHash);

        Assert.That(actual.Result, Is.EqualTo(Result.Success));
        Assert.That(actual.ErrorCode, Is.Zero);
        Assert.That(actual.Data, Is.EqualTo(GetFileNames(blockHash)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StandardTraceBlockToFile_returns_error_when_missing_block(bool isBadBlock)
    {
        Hash256 blockHash = TestItem.KeccakA;

        _blockFinder.FindHeader(blockHash).ReturnsNull();

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        ResultWrapper<IEnumerable<string>> actual = isBadBlock
            ? rpcModule.debug_standardTraceBadBlockToFile(blockHash)
            : rpcModule.debug_standardTraceBlockToFile(blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
        Assert.That(actual.Result.Error, Does.Contain("Cannot find header"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StandardTraceBlockToFile_returns_error_when_state_unavailable(bool isBadBlock)
    {
        Hash256 blockHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithHash(blockHash).WithNumber(100).TestObject;

        _blockFinder.FindHeader(blockHash).Returns(header);
        _blockchainBridge.HasStateForBlock(Arg.Is(header)).Returns(false);

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        ResultWrapper<IEnumerable<string>> actual = isBadBlock
            ? rpcModule.debug_standardTraceBadBlockToFile(blockHash)
            : rpcModule.debug_standardTraceBlockToFile(blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
        Assert.That(actual.Result.Error, Does.Contain("No state available"));
    }

    [Test]
    public void Debug_intermediateRoots_returns_post_tx_roots_from_bridge()
    {
        Hash256 blockHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;
        _blockFinder.FindHeader(blockHash).Returns(header);
        _blockchainBridge.HasStateForBlock(Arg.Is(header)).Returns(true);

        Hash256[] expected = [TestItem.KeccakB, TestItem.KeccakC];
        _debugBridge
            .GetBlockIntermediateRoots(Arg.Is(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions?>())
            .Returns(expected);

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        ResultWrapper<IReadOnlyCollection<Hash256>> actual = rpcModule.debug_intermediateRoots(blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(actual.Data, Is.EqualTo(expected));
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

    [TestCaseSource(nameof(IntermediateRootsErrorCases))]
    public void Debug_intermediateRoots_fails(
        Action<Hash256, IBlockFinder, IBlockchainBridge> setup,
        int expectedErrorCode,
        string? expectedErrorSubstring)
    {
        Hash256 blockHash = TestItem.KeccakA;
        setup(blockHash, _blockFinder, _blockchainBridge);

        DebugRpcModule rpcModule = CreateDebugRpcModule(_debugBridge);
        ResultWrapper<IReadOnlyCollection<Hash256>> actual = rpcModule.debug_intermediateRoots(blockHash);

        Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(actual.ErrorCode, Is.EqualTo(expectedErrorCode));
        if (expectedErrorSubstring is not null)
        {
            Assert.That(actual.Result.Error, Does.Contain(expectedErrorSubstring));
        }
    }
}
