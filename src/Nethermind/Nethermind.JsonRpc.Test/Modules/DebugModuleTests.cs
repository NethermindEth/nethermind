// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing.GethStyle;
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

namespace Nethermind.JsonRpc.Test.Modules;

// Tests with mocked IDebugBridge
[Parallelizable(ParallelScope.Self)]
public class DebugModuleTests
{
    private readonly IJsonRpcConfig jsonRpcConfig = new JsonRpcConfig();
    private readonly ISpecProvider specProvider = Substitute.For<ISpecProvider>();
    private readonly IDebugBridge debugBridge = Substitute.For<IDebugBridge>();
    private readonly IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
    private readonly IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
    private readonly MemDb _blocksDb = new();

    private DebugRpcModule CreateDebugRpcModule(IDebugBridge customDebugBridge)
    {
        return new(
            LimboLogs.Instance,
            customDebugBridge,
            jsonRpcConfig,
            specProvider,
            blockchainBridge,
            new BlocksConfig().SecondsPerSlot,
            blockFinder
        );
    }

    [Test]
    public async Task Get_from_db()
    {
        byte[] key = new byte[] { 1, 2, 3 };
        byte[] value = new byte[] { 4, 5, 6 };
        debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(value);
        _ = Substitute.For<IConfigProvider>();
        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response =
            await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getFromDb", "STATE", key) as JsonRpcSuccessResponse;

        byte[]? result = response?.Result as byte[];
    }

    [Test]
    public async Task Get_from_db_null_value()
    {
        debugBridge.GetDbValue(Arg.Any<string>(), Arg.Any<byte[]>()).Returns((byte[])null!);
        _ = Substitute.For<IConfigProvider>();
        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        byte[] key = new byte[] { 1, 2, 3 };
        using var response =
            await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getFromDb", "STATE", key) as
                JsonRpcSuccessResponse;

        Assert.That(response, Is.Not.Null);
    }

    [TestCase(1)]
    [TestCase(0x1)]
    public async Task Get_chain_level(object parameter)
    {
        debugBridge.GetLevelInfo(1).Returns(
            new ChainLevelInfo(
                true,
                new[]
                {
                    new BlockInfo(TestItem.KeccakA, 1000),
                    new BlockInfo(TestItem.KeccakB, 1001),
                }));

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getChainLevel", parameter) as JsonRpcSuccessResponse;
        var chainLevel = response?.Result as ChainLevelForRpc;
        Assert.That(chainLevel, Is.Not.Null);
        Assert.That(chainLevel?.HasBlockOnMainChain, Is.EqualTo(true));
        Assert.That(chainLevel?.BlockInfos.Length, Is.EqualTo(2));
    }

    [Test]
    public async Task Get_block_rlp_by_hash()
    {
        BlockDecoder decoder = new();
        Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
        debugBridge.GetBlockRlp(new BlockParameter(Keccak.Zero)).Returns(rlp.Bytes);

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlpByHash", Keccak.Zero) as JsonRpcSuccessResponse;
        Assert.That((byte[]?)response?.Result, Is.EqualTo(rlp.Bytes));
    }

    [Test]
    public async Task Get_raw_Header()
    {
        HeaderDecoder decoder = new();
        Block blk = Build.A.Block.WithNumber(0).TestObject;
        Rlp rlp = decoder.Encode(blk.Header);
        debugBridge.GetBlock(new BlockParameter((long)0)).Returns(blk);

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getRawHeader", 0) as JsonRpcSuccessResponse;
        Assert.That((byte[]?)response?.Result, Is.EqualTo(rlp.Bytes));
    }

    [Test]
    public async Task Get_block_rlp()
    {
        BlockDecoder decoder = new();
        IDebugBridge localDebugBridge = Substitute.For<IDebugBridge>();
        Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
        localDebugBridge.GetBlockRlp(new BlockParameter(1)).Returns(rlp.Bytes);

        DebugRpcModule rpcModule = CreateDebugRpcModule(localDebugBridge);
        using var response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlp", 1) as JsonRpcSuccessResponse;

        Assert.That((byte[]?)response?.Result, Is.EqualTo(rlp.Bytes));
    }

    [Test]
    public async Task Get_rawblock()
    {
        BlockDecoder decoder = new();
        IDebugBridge localDebugBridge = Substitute.For<IDebugBridge>();
        Rlp rlp = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
        localDebugBridge.GetBlockRlp(new BlockParameter(1)).Returns(rlp.Bytes);

        DebugRpcModule rpcModule = CreateDebugRpcModule(localDebugBridge);
        using var response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getRawBlock", 1) as JsonRpcSuccessResponse;

        Assert.That((byte[]?)response?.Result, Is.EqualTo(rlp.Bytes));
    }

    [Test]
    public async Task Get_block_rlp_when_missing()
    {
        debugBridge.GetBlockRlp(new BlockParameter(1)).ReturnsNull();

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlp", 1) as JsonRpcErrorResponse;

        Assert.That(response?.Error?.Code, Is.EqualTo(-32001));
    }

    [Test]
    public async Task Get_rawblock_when_missing()
    {
        debugBridge.GetBlockRlp(new BlockParameter(1)).ReturnsNull();

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getRawBlock", 1) as JsonRpcErrorResponse;

        Assert.That(response?.Error?.Code, Is.EqualTo(-32001));
    }

    [Test]
    public async Task Get_block_rlp_by_hash_when_missing()
    {
        BlockDecoder decoder = new();
        _ = decoder.Encode(Build.A.Block.WithNumber(1).TestObject);
        debugBridge.GetBlockRlp(new BlockParameter(Keccak.Zero)).ReturnsNull();

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        using var response = await RpcTest.TestRequest<IDebugRpcModule>(rpcModule, "debug_getBlockRlpByHash", Keccak.Zero) as JsonRpcErrorResponse;

        Assert.That(response?.Error?.Code, Is.EqualTo(-32001));
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
        IBadBlockStore badBlocksStore = null!;
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

        debugBridge.GetBadBlocks().Returns(badBlocksStore.GetAll());

        AddBlockResult result = blockTree.SuggestBlock(block1);
        Assert.That(result, Is.EqualTo(AddBlockResult.InvalidBlock));

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        ResultWrapper<IEnumerable<BadBlock>> blocks = rpcModule.debug_getBadBlocks();
        Assert.That(blocks.Data.Count, Is.EqualTo(1));
        Assert.That(blocks.Data.ElementAt(0).Hash, Is.EqualTo(block1.Hash));
        Assert.That(blocks.Data.ElementAt(0).Block.Difficulty, Is.EqualTo(new UInt256(2)));
    }

    [Test]
    public void Debug_traceCall_test()
    {
        GethTxTraceEntry entry = new();

        entry.Storage = new Dictionary<string, string>
        {
            {"1".PadLeft(64, '0'), "2".PadLeft(64, '0')},
            {"3".PadLeft(64, '0'), "4".PadLeft(64, '0')},
        };

        entry.Memory = new string[]
        {
            "5".PadLeft(64, '0'),
            "6".PadLeft(64, '0')
        };

        entry.Stack = [];
        entry.Opcode = "STOP";
        entry.Gas = 22000;
        entry.GasCost = 1;
        entry.Depth = 1;

        var trace = new GethLikeTxTrace();
        trace.ReturnValue = Bytes.FromHexString("a2");
        trace.Entries.Add(entry);

        GethTraceOptions gtOptions = new();

        Transaction transaction = Build.A.Transaction.WithTo(TestItem.AddressA).WithHash(TestItem.KeccakA).TestObject;
        TransactionForRpc txForRpc = TransactionForRpc.FromTransaction(transaction);

        debugBridge.GetTransactionTrace(Arg.Any<Transaction>(), Arg.Any<BlockParameter>(), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>()).Returns(trace);
        blockFinder.Head.Returns(Build.A.Block.WithNumber(1).TestObject);
        blockFinder.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>()).ReturnsForAnyArgs(Build.A.BlockHeader.WithNumber(1).TestObject);
        blockFinder.FindHeader(Arg.Any<BlockParameter>()).ReturnsForAnyArgs(Build.A.BlockHeader.WithNumber(1).TestObject);
        blockchainBridge.HasStateForRoot(Arg.Any<Hash256>()).Returns(true);

        DebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        ResultWrapper<GethLikeTxTrace> debugTraceCall = rpcModule.debug_traceCall(txForRpc, null, gtOptions);
        var expected = ResultWrapper<GethLikeTxTrace>.Success(
            new GethLikeTxTrace()
            {
                Failed = false,
                Entries = new List<GethTxTraceEntry>()
                {
                    new GethTxTraceEntry()
                    {
                        Gas = 22000,
                        GasCost = 1,
                        Depth = 1,
                        Memory = new string[]
                        {
                            "0000000000000000000000000000000000000000000000000000000000000005",
                            "0000000000000000000000000000000000000000000000000000000000000006"
                        },
                        Opcode = "STOP",
                        ProgramCounter = 0,
                        Stack = [],
                        Storage = new Dictionary<string, string>()
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
                },
                Gas = 0,
                ReturnValue = new byte[] { 162 }
            }
        );

        debugTraceCall.Should().BeEquivalentTo(expected);
    }

    [Test]
    public async Task Migrate_receipts()
    {
        debugBridge.MigrateReceipts(Arg.Any<long>(), Arg.Any<long>()).Returns(true);
        IDebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        string response = await RpcTest.TestSerializedRequest(rpcModule, "debug_migrateReceipts", 100);
        Assert.That(response, Is.Not.Null);
    }

    [Test]
    public async Task Update_head_block()
    {
        debugBridge.UpdateHeadBlock(Arg.Any<Hash256>());
        IDebugRpcModule rpcModule = CreateDebugRpcModule(debugBridge);
        await RpcTest.TestSerializedRequest(rpcModule, "debug_resetHead", TestItem.KeccakA);
        debugBridge.Received().UpdateHeadBlock(TestItem.KeccakA);
    }

    [Test]
    public void StandardTraceBlockToFile()
    {
        var blockHash = Keccak.EmptyTreeHash;

        static IEnumerable<string> GetFileNames(Hash256 hash) =>
            new[] { $"block_{hash.ToShortString()}-0", $"block_{hash.ToShortString()}-1" };

        debugBridge
            .TraceBlockToFile(Arg.Is(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(static c => GetFileNames(c.ArgAt<Hash256>(0)));

        var rpcModule = CreateDebugRpcModule(debugBridge);
        var actual = rpcModule.debug_standardTraceBlockToFile(blockHash);
        var expected = ResultWrapper<IEnumerable<string>>.Success(GetFileNames(blockHash));

        actual.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void StandardTraceBadBlockToFile()
    {
        var blockHash = Keccak.EmptyTreeHash;

        static IEnumerable<string> GetFileNames(Hash256 hash) =>
            new[] { $"block_{hash.ToShortString()}-0", $"block_{hash.ToShortString()}-1" };

        debugBridge
            .TraceBadBlockToFile(Arg.Is(blockHash), Arg.Any<CancellationToken>(), Arg.Any<GethTraceOptions>())
            .Returns(static c => GetFileNames(c.ArgAt<Hash256>(0)));

        var rpcModule = CreateDebugRpcModule(debugBridge);
        var actual = rpcModule.debug_standardTraceBadBlockToFile(blockHash);
        var expected = ResultWrapper<IEnumerable<string>>.Success(GetFileNames(blockHash));

        actual.Should().BeEquivalentTo(expected);
    }
}
