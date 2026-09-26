// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
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
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
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
    public void trace_get_disposes_inner_stream_after_materialization([Values] bool fail)
    {
        TestContext test = new();
        using CancellationTokenSource timeout = new();
        int executions = 0;
        ParityTxTraceStreamingResult<ParityTxTraceFromStore> stream = new(
            (_, _, _) => throw new AssertionException("Should materialize instead of serializing"), timeout, LimboLogs.Instance.GetClassLogger<TraceStoreRpcModuleTests>())
        {
            MaterializeForInProcess = () =>
            {
                executions++;
                if (fail) throw new InvalidOperationException("Replay failed");
                return [];
            }
        };
        test.InnerModule.trace_transaction(TestItem.KeccakA).Returns(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(stream));

        if (fail) Assert.Throws<InvalidOperationException>(() => test.Module.trace_get(TestItem.KeccakA, [0]));
        else
        {
            using ResultWrapper<ParityTxTraceFromStore?> result = test.Module.trace_get(TestItem.KeccakA, [0]);
            Assert.That(result.Data, Is.Null);
        }
        Assert.That(executions, Is.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => _ = timeout.Token);
    }

    [Test]
    public void trace_get_preserves_inner_error([Values(ErrorCodes.ResourceNotFound, ErrorCodes.ResourceUnavailable)] int errorCode)
    {
        TestContext test = new();
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> error =
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail("Trace unavailable", errorCode, isTemporary: true);
        test.InnerModule.trace_transaction(TestItem.KeccakA).Returns(error);

        using ResultWrapper<ParityTxTraceFromStore?> result = test.Module.trace_get(TestItem.KeccakA, [0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.Error, Is.EqualTo("Trace unavailable"));
            Assert.That(result.ErrorCode, Is.EqualTo(errorCode));
            Assert.That(result.IsTemporary, Is.True);
        }
    }

    [Test]
    public void trace_get_selects_stored_trace_by_trace_address([Values] bool streaming)
    {
        TestContext test = new(streaming: streaming);
        ParityTraceAction Call(Address to, params int[] traceAddress) =>
            new() { Type = "call", CallType = "call", From = TestItem.AddressA, To = to, TraceAddress = traceAddress };
        // The root calls B (which calls C) and then D.
        ParityTraceAction root = Call(TestItem.AddressB);
        ParityTraceAction first = Call(TestItem.AddressB, 0);
        first.Subtraces.Add(Call(TestItem.AddressC, 0, 0));
        root.Subtraces.AddRange([first, Call(TestItem.AddressD, 1)]);
        test.DbTrace.Action = root;
        test.Store.Set(test.DbTrace.BlockHash!, new ParityLikeTraceSerializer(LimboLogs.Instance).Serialize(test.DbTraces));

        Address? To(params long[] traceAddress)
        {
            using ResultWrapper<ParityTxTraceFromStore?> result = test.Module.trace_get(test.DbTrace.TransactionHash!, traceAddress);
            return result.Data?.Action?.To;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(To(), Is.EqualTo(TestItem.AddressB));
            Assert.That(To(0, 0), Is.EqualTo(TestItem.AddressC));
            Assert.That(To(1), Is.EqualTo(TestItem.AddressD));
            Assert.That(To(2), Is.Null);
        }
    }

    private static async Task<byte[]> Serialize(JsonRpcResponse response)
    {
        using MemoryStream buffer = new();
        PipeWriter writer = PipeWriter.Create(buffer);
        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        await writer.CompleteAsync();
        return buffer.ToArray();
    }

    [Test]
    public async Task Stored_replay_preserves_output_without_trace(
        [Values("stateDiff", "vmTrace", "")] string selection, [Values] bool blockReplay, [Values] bool streaming)
    {
        TestContext test = new(streaming: streaming);
        test.DbTrace.Output = [42];
        test.DbTrace.Action = new ParityTraceAction { Type = "call", CallType = "call", From = TestItem.AddressA, To = TestItem.AddressB };
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        ParityLikeTxTrace reward = new() { BlockHash = test.DbTrace.BlockHash, Action = new ParityTraceAction { Type = "reward", Author = TestItem.AddressA, RewardType = "block" } };
        test.Store.Set(test.DbTrace.BlockHash!, serializer.Serialize(new[] { test.DbTrace, reward }));
        string[] types = selection.Length == 0 ? [] : [selection];
        using JsonRpcResponse response = blockReplay
            ? test.Module.trace_replayBlockTransactions(BlockParameter.Latest, types)
            : test.Module.trace_replayTransaction(test.DbTrace.TransactionHash!, types);
        byte[] serialized = await Serialize(response);
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement result = document.RootElement.GetProperty("result");
        if (blockReplay)
        {
            Assert.That(result.GetArrayLength(), Is.EqualTo(1));
            result = result[0];
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("output").GetString(), Is.EqualTo("0x2a"));
            Assert.That(result.GetProperty("trace").GetArrayLength(), Is.Zero);
        }
    }

    [Test]
    public async Task Stored_replay_keeps_reward_actions_like_live_replay(
        [Values("rewards", "stateDiff,rewards")] string selection, [Values] bool streaming)
    {
        TestContext test = new(streaming: streaming);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = test.BlockFinder.Head!.WithReplacedBody(new BlockBody([tx], []));
        Replay(new DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer>(
            new ParityLikeBlockTracer(new TraceStoreConfig().TraceTypes), test.Store, new ParityLikeTraceSerializer(LimboLogs.Instance), LimboLogs.Instance), block);
        string[] types = selection.Split(',');
        Assert.That(TraceRpcModule.TryGetParityTypes(types, out ParityTraceTypes liveTypes), Is.True);
        JToken expected;
        if (streaming)
        {
            ArrayBufferWriter<byte> sink = new();
            using (Utf8JsonWriter liveWriter = new(sink))
            {
                liveWriter.WriteStartArray();
                using StreamingParityLikeBlockTracer live = new(liveTypes, ParityTraceStreamMode.Replay, includeTxHash: true, liveWriter, null, CancellationToken.None);
                Replay(live, block);
                liveWriter.WriteEndArray();
            }
            expected = JToken.Parse(Encoding.UTF8.GetString(sink.WrittenSpan));
        }
        else
        {
            ParityLikeBlockTracer live = new(liveTypes);
            Replay(live, block);
            expected = JToken.Parse(Serializer.Serialize(live.BuildResult().Select(static t => new ParityTxTraceFromReplay(t, true))));
        }

        using JsonRpcResponse response = test.Module.trace_replayBlockTransactions(BlockParameter.Latest, types);
        byte[] serialized = await Serialize(response);
        JToken actual = JToken.Parse(Encoding.UTF8.GetString(serialized))["result"]!;

        // The default store records Trace|Rewards only, so it has no state diff to compare against live replay.
        foreach (JObject entry in expected.Children<JObject>().Concat(actual.Children<JObject>())) entry.Remove("stateDiff");
        Assert.That(actual, Is.EqualTo(expected).Using(JToken.EqualityComparer));
        Assert.That(actual.Last!["trace"]!.Single()["action"]!["rewardType"]!.Value<string>(), Is.EqualTo("block"));
    }

    // Mirrors BlockProcessor: transactions, then the reward on a null transaction, reported after its trace ends.
    private static void Replay(IBlockTracer tracer, Block block)
    {
        byte[] output = [42];
        tracer.StartNewBlockTrace(block);
        foreach (Transaction tx in block.Transactions)
        {
            ITxTracer txTracer = tracer.StartNewTxTrace(tx);
            if (txTracer.IsTracingActions)
            {
                txTracer.ReportAction(21_000, tx.Value, tx.SenderAddress!, tx.To!, tx.Data, ExecutionType.TRANSACTION);
                txTracer.ReportActionEnd(0, output);
            }
            txTracer.MarkAsSuccess(tx.To!, default, output, []);
            tracer.EndTxTrace();
        }
        tracer.StartNewTxTrace(null);
        tracer.EndTxTrace();
        tracer.ReportReward(TestItem.AddressC, "block", 2);
        tracer.EndBlockTrace();
    }

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
    public void trace_replayTransaction_defers_unknown_trace_type_to_inner_module()
    {
        TestContext test = new();
        string[] traceTypes = ["unknown"];

        test.Module.trace_replayTransaction(test.DbTrace.TransactionHash!, traceTypes);

        test.InnerModule.Received(1).trace_replayTransaction(test.DbTrace.TransactionHash!, traceTypes, false);
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

    [Test]
    public void trace_filter_returns_from_inner_module_when_any_block_trace_is_missing([Values(0, 1, 2)] int parallelization)
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

    [Test]
    public void trace_block_filters_reward_traces_from_store()
    {
        TestContext test = new(includeRewardTrace: true);

        ParityTxTraceFromStore[] traces = test.Module.trace_block(BlockParameter.Latest).Data.ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces, Has.Length.EqualTo(1));
            Assert.That(traces.Select(static trace => trace.Type), Is.All.EqualTo("call"));
        }

        test.InnerModule.DidNotReceive().trace_block(BlockParameter.Latest);
    }

    [Test]
    public async Task trace_block_filters_reward_traces_from_store_when_streaming()
    {
        TestContext test = new(includeRewardTrace: true);
        test.JsonRpcConfig.EnableTracingStreamMode = true;

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = test.Module.trace_block(BlockParameter.Latest);
        Assert.That(result.Data, Is.AssignableTo<IStreamableResult>());

        byte[] json = await WriteStreamableAsync((IStreamableResult)result.Data);
        JArray traces = JArray.Parse(Encoding.UTF8.GetString(json));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces, Has.Count.EqualTo(1));
            Assert.That(traces.Select(static trace => trace["type"]!.Value<string>()), Is.All.EqualTo("call"));
        }

        test.InnerModule.DidNotReceive().trace_block(BlockParameter.Latest);
    }

    private static async Task<byte[]> WriteStreamableAsync(IStreamableResult result)
    {
        using MemoryStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        await result.WriteToAsync(writer, CancellationToken.None);
        await writer.FlushAsync();
        await writer.CompleteAsync();
        return stream.ToArray();
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
        public JsonRpcConfig JsonRpcConfig { get; }
        public TraceStoreRpcModule Module { get; }

        public TestContext(int parallelization = 0, bool streaming = true, bool includeRewardTrace = false)
        {
            InnerModule = Substitute.For<ITraceRpcModule>();
            Store = new MemDb();
            BlockFinder = Build.A.BlockTree().OfChainLength(3).TestObject;
            ReceiptFinder = Substitute.For<IReceiptFinder>();
            ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
            JsonRpcConfig = new JsonRpcConfig { EnableTracingStreamMode = streaming };
            Module = new TraceStoreRpcModule(InnerModule, Store, BlockFinder, ReceiptFinder, serializer, JsonRpcConfig, LimboLogs.Instance, parallelization);
            Hash256 dbTransaction = Build.A.Transaction.TestObject.Hash!;
            Hash256 dbBlock = BlockFinder.Head!.Hash!;
            DbTrace = includeRewardTrace ? BuildCallTrace(dbBlock, dbTransaction) : new() { BlockHash = dbBlock, TransactionHash = dbTransaction };
            DbTraces = includeRewardTrace ? [DbTrace, BuildRewardTrace(dbBlock)] : [DbTrace];
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

            InnerModule.trace_transaction(nonDbTransaction)
                .Returns(nonDbFromStoreWrapper);

        }

        private static ParityLikeTxTrace BuildRewardTrace(Hash256 blockHash) =>
            new()
            {
                BlockHash = blockHash,
                Action = new ParityTraceAction
                {
                    Type = "reward",
                    Author = TestItem.AddressA,
                    RewardType = "block",
                }
            };

        private static ParityLikeTxTrace BuildCallTrace(Hash256 blockHash, Hash256 txHash) =>
            new()
            {
                BlockHash = blockHash,
                TransactionHash = txHash,
                Action = new ParityTraceAction
                {
                    Type = "call",
                    CallType = "call",
                }
            };
    }
}
