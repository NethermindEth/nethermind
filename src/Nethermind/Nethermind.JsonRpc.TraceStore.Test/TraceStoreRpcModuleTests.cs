// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Tests;

[Parallelizable(ParallelScope.All)]
public class TraceStoreRpcModuleTests
{
    [Test]
    public void trace_call_returns_from_inner_module()
    {
        TestContext test = new();

        test.Module.trace_call(new TransactionForRpc(Build.A.Transaction.TestObject), new[] { ParityTraceTypes.Trace.ToString() }, BlockParameter.Latest)
            .Should().BeEquivalentTo(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0])));
    }

    [Test]
    public void trace_callMany_returns_from_inner_module()
    {
        TestContext test = new();

        TransactionForRpcWithTraceTypes[] calls = { new() { TraceTypes = new[] { ParityTraceTypes.Trace.ToString() }, Transaction = new TransactionForRpc(Build.A.Transaction.TestObject) } };
        test.Module.trace_callMany(calls, BlockParameter.Latest)
            .Should().BeEquivalentTo(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.NonDbTraces.Select(t => new ParityTxTraceFromReplay(t))));
    }

    [Test]
    public void trace_Transaction_returns_from_inner_module()
    {
        TestContext test = new();

        test.Module.trace_rawTransaction(Bytes.Empty, new[] { ParityTraceTypes.Trace.ToString() })
            .Should().BeEquivalentTo(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0])));
    }

    [Test]
    public void trace_replayTransaction_returns_from_inner_module()
    {
        TestContext test = new();

        test.Module.trace_replayTransaction(test.NonDbTraces.First().TransactionHash!, new[] { ParityTraceTypes.Trace.ToString() })
            .Should().BeEquivalentTo(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0])));
    }

    [Test]
    public void trace_replayTransaction_returns_from_store()
    {
        TestContext test = new();

        test.Module.trace_replayTransaction(test.DbTrace.TransactionHash!, new[] { ParityTraceTypes.Trace.ToString() })
            .Should().BeEquivalentTo(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.DbTrace)));
    }

    [Test]
    public void trace_replayBlockTransactions_returns_from_inner_module()
    {
        TestContext test = new();

        test.Module.trace_replayBlockTransactions(new BlockParameter(1), new[] { ParityTraceTypes.Trace.ToString() })
            .Should().BeEquivalentTo(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.NonDbTraces.Select(t => new ParityTxTraceFromReplay(t))));
    }

    [Test]
    public void trace_replayBlockTransactions_returns_from_store()
    {
        TestContext test = new();

        test.Module.trace_replayBlockTransactions(BlockParameter.Latest, new[] { ParityTraceTypes.Trace.ToString(), ParityTraceTypes.Rewards.ToString() })
            .Should().BeEquivalentTo(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.DbTraces.Select(t => new ParityTxTraceFromReplay(t))));
    }

    [Test]
    public void trace_filter_returns_from_inner_module()
    {
        TestContext test = new();

        test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = new BlockParameter(1) })
            .Should().BeEquivalentTo(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.NonDbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace)));
    }

    [Test]
    public void trace_filter_returns_from_store()
    {
        TestContext test = new();

        test.Module.trace_filter(new TraceFilterForRpc { FromBlock = BlockParameter.Latest, ToBlock = BlockParameter.Latest })
            .Should().BeEquivalentTo(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.DbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace)));
    }

    private class TestContext
    {
        public ParityLikeTxTrace DbTrace { get; }
        public ParityLikeTxTrace[] DbTraces { get; }
        public ParityLikeTxTrace[] NonDbTraces { get; }
        public ITraceRpcModule InnerModule { get; }
        public MemDb Store { get; }
        public IBlockFinder BlockFinder { get; }
        public IReceiptFinder ReceiptFinder { get; }
        public TraceStoreRpcModule Module { get; }

        public TestContext()
        {
            InnerModule = Substitute.For<ITraceRpcModule>();
            Store = new MemDb();
            BlockFinder = Build.A.BlockTree().OfChainLength(3).TestObject;
            ReceiptFinder = Substitute.For<IReceiptFinder>();
            ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
            Module = new TraceStoreRpcModule(InnerModule, Store, BlockFinder, ReceiptFinder, serializer, LimboLogs.Instance);
            Keccak dbTransaction = Build.A.Transaction.TestObject.Hash!;
            Keccak dbBlock = BlockFinder.Head!.Hash!;
            DbTrace = new() { BlockHash = dbBlock, TransactionHash = dbTransaction };
            DbTraces = new[] { DbTrace };
            Keccak nonDbTransaction = TestItem.KeccakA;
            NonDbTraces = new[] { new ParityLikeTxTrace() { BlockHash = dbBlock, TransactionHash = nonDbTransaction } };
            Store.Set(dbBlock, serializer.Serialize(DbTraces));
            ReceiptFinder.FindBlockHash(dbTransaction).Returns(dbBlock);
            ReceiptFinder.FindBlockHash(nonDbTransaction).Returns(dbBlock);

            ResultWrapper<ParityTxTraceFromReplay> nonDbReplayWrapper = ResultWrapper<ParityTxTraceFromReplay>.Success(new(NonDbTraces[0]));
            ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> nonDbReplaysWrapper = ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(NonDbTraces.Select(t => new ParityTxTraceFromReplay(t)));

            InnerModule.trace_call(Arg.Any<TransactionForRpc>(), Arg.Any<string[]>(), Arg.Any<BlockParameter>())
                .Returns(nonDbReplayWrapper);

            InnerModule.trace_callMany(Arg.Any<TransactionForRpcWithTraceTypes[]>(), Arg.Any<BlockParameter>())
                .Returns(nonDbReplaysWrapper);

            InnerModule.trace_rawTransaction(Arg.Any<byte[]>(), Arg.Any<string[]>())
                .Returns(nonDbReplayWrapper);

            InnerModule.trace_replayTransaction(nonDbTransaction, Arg.Any<string[]>())
                .Returns(nonDbReplayWrapper);

            InnerModule.trace_replayBlockTransactions(Arg.Any<BlockParameter>(), Arg.Any<string[]>())
                .Returns(nonDbReplaysWrapper);

            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> nonDbFromStoreWrapper = ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(NonDbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace));
            InnerModule.trace_filter(Arg.Any<TraceFilterForRpc>())
                .Returns(nonDbFromStoreWrapper);

            InnerModule.trace_block(BlockParameter.Latest)
                .Returns(nonDbFromStoreWrapper);

            InnerModule.trace_get(nonDbTransaction, new[] { 0L })
                .Returns(nonDbFromStoreWrapper);

            InnerModule.trace_transaction(nonDbTransaction)
                .Returns(nonDbFromStoreWrapper);

        }
    }
}
