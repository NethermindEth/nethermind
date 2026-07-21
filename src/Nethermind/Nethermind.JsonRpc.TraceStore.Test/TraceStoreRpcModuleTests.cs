// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.TraceStore.Test;

[Parallelizable(ParallelScope.All)]
public class TraceStoreRpcModuleTests
{
    private static readonly EthereumJsonSerializer Serializer = new();

    [Test]
    public void trace_call_returns_from_inner_module()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_call(
                call: TransactionForRpc.FromTransaction(Build.A.Transaction.TestObject),
                traceTypes: [ParityTraceTypes.Trace.ToString()],
                blockParameter: BlockParameter.Latest))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0]))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_callMany_returns_from_inner_module()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_callMany(
                new(new(1) { new() { TraceTypes = [nameof(ParityTraceTypes.Trace)], Transaction = TransactionForRpc.FromTransaction(Build.A.Transaction.TestObject) } }),
                BlockParameter.Latest))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.NonDbTraces.Select(static t => new ParityTxTraceFromReplay(t)))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_Transaction_returns_from_inner_module()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_rawTransaction(Bytes.Empty, new[] { ParityTraceTypes.Trace.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0]))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_replayTransaction_returns_from_inner_module()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_replayTransaction(test.NonDbTraces.First().TransactionHash!, new[] { ParityTraceTypes.Trace.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0]))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_replayTransaction_returns_from_store()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_replayTransaction(test.DbTrace.TransactionHash!, new[] { ParityTraceTypes.Trace.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.DbTrace))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_replayBlockTransactions_returns_from_inner_module()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_replayBlockTransactions(new BlockParameter(1), new[] { ParityTraceTypes.Trace.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.NonDbTraces.Select(static t => new ParityTxTraceFromReplay(t)))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_replayBlockTransactions_returns_from_store()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_replayBlockTransactions(BlockParameter.Latest, new[] { ParityTraceTypes.Trace.ToString(), ParityTraceTypes.Rewards.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.DbTraces.Select(static t => new ParityTxTraceFromReplay(t)))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_filter_returns_from_inner_module()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = new BlockParameter(1) }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.NonDbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_filter_returns_from_store()
    {
        TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_filter(new TraceFilterForRpc { FromBlock = BlockParameter.Latest, ToBlock = BlockParameter.Latest }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.DbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace))))).Using(JToken.EqualityComparer));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void trace_filter_returns_from_inner_module_when_any_block_trace_is_missing(int parallelization)
    {
        TestContext test = new(parallelization: parallelization);

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = BlockParameter.Latest }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.NonDbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace))))).Using(JToken.EqualityComparer));

        test.InnerModule.Received().trace_filter(Arg.Any<TraceFilterForRpc>());
    }

    [Test]
    public async Task trace_block_to_async_stream()
    {
        TestContext test = new();

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = test.Module.trace_block(BlockParameter.Latest);
        Assert.That(result.Data, Is.AssignableTo<IStreamableResult>());
        IStreamableResult streaming = (IStreamableResult)result.Data;

        await using AsyncCompletingStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream);

        Assert.DoesNotThrowAsync(async () => await streaming.WriteToAsync(writer, CancellationToken.None));

        await writer.CompleteAsync();
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

        public TestContext(int parallelization = 0)
        {
            InnerModule = Substitute.For<ITraceRpcModule>();
            Store = new MemDb();
            BlockFinder = Build.A.BlockTree().OfChainLength(3).TestObject;
            ReceiptFinder = Substitute.For<IReceiptFinder>();
            ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
            Module = new TraceStoreRpcModule(InnerModule, Store, BlockFinder, ReceiptFinder, serializer, new JsonRpcConfig(), LimboLogs.Instance, parallelization);
            Hash256 dbTransaction = Build.A.Transaction.TestObject.Hash!;
            Hash256 dbBlock = BlockFinder.Head!.Hash!;
            DbTrace = new() { BlockHash = dbBlock, TransactionHash = dbTransaction };
            DbTraces = new[] { DbTrace };
            Hash256 nonDbTransaction = TestItem.KeccakA;
            NonDbTraces = new[] { new ParityLikeTxTrace() { BlockHash = dbBlock, TransactionHash = nonDbTransaction } };
            Store.Set(dbBlock, serializer.Serialize(DbTraces));
            ReceiptFinder.FindBlockHash(dbTransaction).Returns(dbBlock);
            ReceiptFinder.FindBlockHash(nonDbTransaction).Returns(dbBlock);

            ResultWrapper<ParityTxTraceFromReplay> nonDbReplayWrapper = ResultWrapper<ParityTxTraceFromReplay>.Success(new(NonDbTraces[0]));
            ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> nonDbReplaysWrapper = ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(NonDbTraces.Select(static t => new ParityTxTraceFromReplay(t)));

            InnerModule.trace_call(Arg.Any<TransactionForRpc>(), Arg.Any<string[]>(), Arg.Any<BlockParameter>())
                .Returns(nonDbReplayWrapper);

            InnerModule.trace_callMany(Arg.Any<TraceCallManyRequest>(), Arg.Any<BlockParameter>())
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
