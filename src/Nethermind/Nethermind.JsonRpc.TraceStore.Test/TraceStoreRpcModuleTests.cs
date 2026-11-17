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
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Exceptions;
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
        using TestContext test = new();
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
        test.InnerModule.trace_transaction(TestItem.KeccakA).Returns(ResultWrapper<IEnumerable<ParityTxTraceFromStore>?>.Success(stream));

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
        using TestContext test = new();
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> error =
            ResultWrapper<IEnumerable<ParityTxTraceFromStore>?>.Fail("Trace unavailable", errorCode, isTemporary: true);
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
    public void transaction_lookups_return_null_for_missing_transaction()
    {
        using TestContext test = new();
        test.InnerModule.trace_transaction(TestItem.KeccakB).Returns(ResultWrapper<IEnumerable<ParityTxTraceFromStore>?>.Success(null));
        test.InnerModule.trace_replayTransaction(TestItem.KeccakB, Arg.Any<string[]>()).Returns(ResultWrapper<ParityTxTraceFromReplay?>.Success(null));

        using ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> traces = test.Module.trace_transaction(TestItem.KeccakB);
        using ResultWrapper<ParityTxTraceFromStore?> trace = test.Module.trace_get(TestItem.KeccakB, []);
        using ResultWrapper<ParityTxTraceFromReplay?> replay = test.Module.trace_replayTransaction(TestItem.KeccakB, ["trace"]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(traces.Data, Is.Null);
            Assert.That(trace.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(trace.Data, Is.Null);
            Assert.That(replay.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(replay.Data, Is.Null);
        }
    }

    [Test]
    public void trace_get_selects_stored_trace_by_trace_address([Values] bool streaming)
    {
        using TestContext test = new(streaming: streaming);
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

    [Test]
    public void Stored_trace_of_non_canonical_block_requires_trace_non_canonical([Values] bool traceNonCanonical, [Values] bool replay)
    {
        using TestContext test = new(streaming: false);
        IBlockTree blockTree = test.BlockFinder;
        Block orphan = Build.A.Block.WithParent(blockTree.FindParent(blockTree.Head!, BlockTreeLookupOptions.None)!).WithExtraData([1]).TestObject;
        blockTree.SuggestBlock(orphan);
        Hash256 txHash = TestItem.KeccakG;
        test.ReceiptFinder.FindBlockHash(txHash).Returns(orphan.Hash);
        test.Store.Set(orphan.Hash!, new ParityLikeTraceSerializer(LimboLogs.Instance).Serialize([new ParityLikeTxTrace { BlockHash = orphan.Hash, TransactionHash = txHash }]));

        if (replay)
        {
            using ResultWrapper<ParityTxTraceFromReplay?> result = test.Module.trace_replayTransaction(txHash, ["trace"], traceNonCanonical);
            test.InnerModule.Received(traceNonCanonical ? 0 : 1).trace_replayTransaction(txHash, Arg.Any<string[]>(), traceNonCanonical);
        }
        else
        {
            using ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> result = test.Module.trace_transaction(txHash, traceNonCanonical);
            test.InnerModule.Received(traceNonCanonical ? 0 : 1).trace_transaction(txHash, traceNonCanonical);
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

    private static async Task<JsonElement> ReplayStored(TestContext test, string[] types, bool blockReplay)
    {
        using JsonRpcResponse response = blockReplay
            ? test.Module.trace_replayBlockTransactions(BlockParameter.Latest, types)
            : test.Module.trace_replayTransaction(test.DbTrace.TransactionHash!, types);
        using JsonDocument document = JsonDocument.Parse(await Serialize(response));
        JsonElement result = document.RootElement.GetProperty("result");
        if (blockReplay)
        {
            Assert.That(result.GetArrayLength(), Is.EqualTo(1));
            result = result[0];
        }

        return result.Clone();
    }

    [Test]
    public async Task Stored_replay_preserves_output_without_trace(
        [Values("stateDiff", "vmTrace", "")] string selection, [Values] bool blockReplay, [Values] bool streaming)
    {
        using TestContext test = new(streaming: streaming);
        test.DbTrace.Output = [42];
        test.DbTrace.Action = new ParityTraceAction { Type = "call", CallType = "call", From = TestItem.AddressA, To = TestItem.AddressB };
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        ParityLikeTxTrace reward = new() { BlockHash = test.DbTrace.BlockHash, Action = new ParityTraceAction { Type = "reward", Author = TestItem.AddressA, RewardType = "block" } };
        test.Store.Set(test.DbTrace.BlockHash!, serializer.Serialize(new[] { test.DbTrace, reward }));
        JsonElement result = await ReplayStored(test, selection.Length == 0 ? [] : [selection], blockReplay);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("output").GetString(), Is.EqualTo("0x2a"));
            Assert.That(result.GetProperty("trace").GetArrayLength(), Is.Zero);
        }
    }

    // Live replay builds vmTrace only when it is requested, and then records ex.store at every depth, stateDiff or not.
    [Test]
    public async Task Stored_replay_selects_vm_trace_like_live_replay(
        [Values("trace", "stateDiff", "vmTrace", "vmTrace,stateDiff")] string selection, [Values] bool blockReplay, [Values] bool streaming)
    {
        using TestContext test = new(streaming: streaming);
        test.DbTrace.Action = new ParityTraceAction { Type = "call", CallType = "call", From = TestItem.AddressA, To = TestItem.AddressB };
        test.DbTrace.StateChanges = new() { [TestItem.AddressA] = new ParityAccountStateChange { Balance = new ParityStateChange<UInt256?>(1, 2) } };
        ParityVmTrace sub = new() { Code = [], Operations = [new ParityVmOperationTrace { Store = new ParityStorageChangeTrace { Key = [2], Value = [2] } }] };
        test.DbTrace.VmTrace = new ParityVmTrace { Code = [], Operations = [new ParityVmOperationTrace { Store = new ParityStorageChangeTrace { Key = [1], Value = [1] }, Sub = sub }] };
        test.Store.Set(test.DbTrace.BlockHash!, new ParityLikeTraceSerializer(LimboLogs.Instance).Serialize(new[] { test.DbTrace }));
        string[] types = selection.Split(',');
        JsonElement result = await ReplayStored(test, types, blockReplay);

        JsonElement vmTrace = result.GetProperty("vmTrace");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("stateDiff").ValueKind, Is.EqualTo(types.Contains("stateDiff") ? JsonValueKind.Object : JsonValueKind.Null));
            if (types.Contains("vmTrace"))
            {
                JsonElement operation = vmTrace.GetProperty("ops")[0];
                Assert.That(operation.GetProperty("ex").GetProperty("store").GetProperty("key").GetString(), Is.EqualTo("0x1"));
                Assert.That(operation.GetProperty("sub").GetProperty("ops")[0].GetProperty("ex").GetProperty("store").GetProperty("key").GetString(), Is.EqualTo("0x2"));
            }
            else
            {
                Assert.That(vmTrace.ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
        }
    }

    [Test]
    public async Task Stored_replay_keeps_reward_actions_like_live_replay(
        [Values("rewards", "trace,rewards")] string selection, [Values] bool streaming)
    {
        ParityTraceTypes storedTypes = new TraceStoreConfig().TraceTypes;
        using TestContext test = new(streaming: streaming, storedTraceTypes: storedTypes);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = test.BlockFinder.Head!.WithReplacedBody(new BlockBody([tx], []));
        Replay(new DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer>(
            new ParityLikeBlockTracer(storedTypes), test.Store, new ParityLikeTraceSerializer(LimboLogs.Instance), LimboLogs.Instance), block);
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

        Assert.That(actual, Is.EqualTo(expected).Using(JToken.EqualityComparer));
        Assert.That(actual.Last!["trace"]!.Single()["action"]!["rewardType"]!.Value<string>(), Is.EqualTo("block"));
    }

    // A store answers only for the types it records; anything else goes to live tracing rather than returning nulls.
    [TestCase("Trace, Rewards", "trace", false, true)]
    [TestCase("Trace, Rewards", "trace,rewards", true, true)]
    [TestCase("Trace, Rewards", "vmTrace", false, false)]
    [TestCase("Trace, Rewards", "vmTrace", true, false)]
    [TestCase("Trace, Rewards", "stateDiff", false, false)]
    [TestCase("Trace, Rewards", "stateDiff", true, false)]
    [TestCase("Trace", "trace,rewards", true, false)]
    [TestCase("VmTrace, StateDiff, Trace", "vmTrace,stateDiff", true, true)]
    // Live replay can answer a transaction with its block's reward, which the store doesn't hold per transaction.
    [TestCase("Trace, Rewards", "trace,rewards", false, false)]
    public void Stored_replay_serves_only_recorded_types(string stored, string selection, bool blockReplay, bool fromStore)
    {
        using TestContext test = new(streaming: false, storedTraceTypes: Enum.Parse<ParityTraceTypes>(stored));
        string[] types = selection.Split(',');

        if (blockReplay)
        {
            using ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> result = test.Module.trace_replayBlockTransactions(BlockParameter.Latest, types);
            test.InnerModule.Received(fromStore ? 0 : 1).trace_replayBlockTransactions(Arg.Any<BlockParameter>(), Arg.Any<string[]>());
        }
        else
        {
            using ResultWrapper<ParityTxTraceFromReplay?> result = test.Module.trace_replayTransaction(test.DbTrace.TransactionHash!, types);
            test.InnerModule.Received(fromStore ? 0 : 1).trace_replayTransaction(Arg.Any<Hash256>(), Arg.Any<string[]>(), Arg.Any<bool>());
        }
    }

    // Live trace_transaction traces calls; live trace_block and trace_filter also trace rewards.
    [TestCase("Trace, Rewards", true, true)]
    [TestCase("Trace", true, false)]
    [TestCase("StateDiff, Rewards", false, false)]
    public void Stored_actions_serve_only_recorded_types(string stored, bool transactionFromStore, bool blockFromStore)
    {
        using TestContext test = new(streaming: false, storedTraceTypes: Enum.Parse<ParityTraceTypes>(stored));

        using ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> transaction = test.Module.trace_transaction(test.DbTrace.TransactionHash!);
        using ResultWrapper<IEnumerable<ParityTxTraceFromStore>> block = test.Module.trace_block(BlockParameter.Latest);
        using ResultWrapper<IEnumerable<ParityTxTraceFromStore>> filter = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = BlockParameter.Latest, ToBlock = BlockParameter.Latest });

        test.InnerModule.Received(transactionFromStore ? 0 : 1).trace_transaction(Arg.Any<Hash256>(), Arg.Any<bool>());
        test.InnerModule.Received(blockFromStore ? 0 : 1).trace_block(Arg.Any<BlockParameter>(), Arg.Any<string?>());
        test.InnerModule.Received(blockFromStore ? 0 : 1).trace_filter(Arg.Any<TraceFilterForRpc>());
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
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_call(
                call: TransactionForRpc.FromTransaction(Build.A.Transaction.TestObject),
                traceTypes: [ParityTraceTypes.Trace.ToString()],
                blockParameter: BlockParameter.Latest))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0]))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_callMany_returns_from_inner_module()
    {
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_callMany(
                new(new(1) { new() { TraceTypes = [nameof(ParityTraceTypes.Trace)], Transaction = TransactionForRpc.FromTransaction(Build.A.Transaction.TestObject) } }),
                BlockParameter.Latest))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.NonDbTraces.Select(static t => new ParityTxTraceFromReplay(t)))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_Transaction_returns_from_inner_module()
    {
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_rawTransaction(Bytes.Empty, new[] { ParityTraceTypes.Trace.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0]))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_replayTransaction_returns_from_inner_module()
    {
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_replayTransaction(test.NonDbTraces.First().TransactionHash!, new[] { ParityTraceTypes.Trace.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.NonDbTraces[0]))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_replayTransaction_returns_from_store()
    {
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_replayTransaction(test.DbTrace.TransactionHash!, new[] { ParityTraceTypes.Trace.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplay(test.DbTrace))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_replayTransaction_defers_unknown_trace_type_to_inner_module()
    {
        using TestContext test = new();
        string[] traceTypes = ["unknown"];

        test.Module.trace_replayTransaction(test.DbTrace.TransactionHash!, traceTypes);

        test.InnerModule.Received(1).trace_replayTransaction(test.DbTrace.TransactionHash!, traceTypes, false);
    }

    [Test]
    public void trace_replayBlockTransactions_returns_from_inner_module()
    {
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_replayBlockTransactions(new BlockParameter(1), new[] { ParityTraceTypes.Trace.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.NonDbTraces.Select(static t => new ParityTxTraceFromReplay(t)))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_replayBlockTransactions_returns_from_store()
    {
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_replayBlockTransactions(BlockParameter.Latest, new[] { ParityTraceTypes.Trace.ToString(), ParityTraceTypes.Rewards.ToString() }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(test.DbTraces.Select(static t => new ParityTxTraceFromReplay(t)))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_filter_returns_from_inner_module()
    {
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = new BlockParameter(1) }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.NonDbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public void trace_filter_returns_from_store()
    {
        using TestContext test = new();

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_filter(new TraceFilterForRpc { FromBlock = BlockParameter.Latest, ToBlock = BlockParameter.Latest }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.DbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace))))).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task trace_filter_from_store_reads_stored_traces_before_return_unless_streamed([Values] bool streaming)
    {
        CountingTraceSerializer serializer = new(new ParityLikeTraceSerializer(LimboLogs.Instance));
        using TestContext test = new(streaming: streaming, serializer: serializer);
        test.Store.Set(test.BlockFinder.FindHeader(new BlockParameter(1))!.Hash!, serializer.Serialize(test.DbTraces));

        using JsonRpcResponse response = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = BlockParameter.Latest });
        int readBeforeWrite = serializer.DeserializeCount;
        await Serialize(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readBeforeWrite, Is.EqualTo(streaming ? 0 : 2));
            Assert.That(serializer.DeserializeCount, Is.EqualTo(2));
            test.InnerModule.DidNotReceive().trace_filter(Arg.Any<TraceFilterForRpc>());
        }
    }

    [Test]
    public async Task trace_filter_from_store_stops_reading_stored_traces_when_count_is_reached([Values] bool streaming)
    {
        const int windowSize = 64;
        CountingTraceSerializer serializer = new(new ParityLikeTraceSerializer(LimboLogs.Instance));
        using TestContext test = new(parallelization: 1, streaming: streaming, chainLength: 2 * windowSize + 2, serializer: serializer);
        StoreCallOnEveryBlock(test, serializer);

        using JsonRpcResponse response = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = BlockParameter.Latest, Count = windowSize });
        JToken result = JToken.Parse(Encoding.UTF8.GetString(await Serialize(response)))["result"]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Count(), Is.EqualTo(windowSize));
            Assert.That(serializer.DeserializeCount, Is.EqualTo(windowSize));
        }
    }

    [Test]
    public async Task trace_filter_from_store_traces_block_live_when_stored_traces_are_gone_before_write()
    {
        using TestContext test = new(streaming: true);
        Hash256 head = test.BlockFinder.Head!.Hash!;
        ParityLikeTxTrace liveTrace = new() { BlockHash = head, TransactionHash = TestItem.KeccakB, Action = Call(TestItem.AddressA, TestItem.AddressC) };
        test.InnerModule.trace_block(Arg.Is<BlockParameter>(p => p.BlockHash == head), Arg.Any<string?>())
            .Returns(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(ParityTxTraceFromStore.FromTxTrace(liveTrace).ToArray()));

        using JsonRpcResponse response = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = BlockParameter.Latest, ToBlock = BlockParameter.Latest });
        test.Store.Remove(head.Bytes);
        JToken result = JToken.Parse(Encoding.UTF8.GetString(await Serialize(response)))["result"]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(JToken.Parse(Serializer.Serialize(ParityTxTraceFromStore.FromTxTrace(liveTrace)))).Using(JToken.EqualityComparer));
            test.InnerModule.DidNotReceive().trace_filter(Arg.Any<TraceFilterForRpc>());
        }
    }

    [Test]
    public async Task trace_filter_from_store_traces_unreadable_blocks_live_before_return_unless_streamed([Values] bool streaming)
    {
        using StoreWithUnreadableBlocks test = new(streaming, readableBlocks: 0);
        ParityLikeTxTrace liveTrace = new() { BlockHash = test.Hashes[0], TransactionHash = TestItem.KeccakB, Action = Call(TestItem.AddressA, TestItem.AddressC) };
        test.InnerModule.trace_block(Arg.Any<BlockParameter>(), Arg.Any<string?>())
            .Returns(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(ParityTxTraceFromStore.FromTxTrace(liveTrace).ToArray()));

        using JsonRpcResponse response = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = new BlockParameter(1) });
        int liveBeforeWrite = test.InnerModule.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == nameof(ITraceRpcModule.trace_block));
        JToken result = JToken.Parse(Encoding.UTF8.GetString(await Serialize(response)))["result"]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(liveBeforeWrite, Is.EqualTo(streaming ? 0 : 1));
            Assert.That(result, Is.EqualTo(JToken.Parse(Serializer.Serialize(ParityTxTraceFromStore.FromTxTrace(liveTrace)))).Using(JToken.EqualityComparer));
        }
    }

    [Test]
    public async Task trace_filter_from_store_does_not_trace_live_after_count_is_reached([Values] bool streaming)
    {
        using StoreWithUnreadableBlocks test = new(streaming, readableBlocks: 1);

        using JsonRpcResponse response = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = new BlockParameter(2), Count = 1 });
        JToken result = JToken.Parse(Encoding.UTF8.GetString(await Serialize(response)))["result"]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Count(), Is.EqualTo(1));
            test.InnerModule.DidNotReceive().trace_block(Arg.Any<BlockParameter>(), Arg.Any<string?>());
        }
    }

    [Test]
    public async Task trace_filter_from_store_stops_live_tracing_when_the_request_times_out([Values] bool streaming)
    {
        const int timeoutMs = 50;
        using StoreWithUnreadableBlocks test = new(streaming, readableBlocks: 0, timeoutMs);
        test.InnerModule.trace_block(Arg.Any<BlockParameter>(), Arg.Any<string?>())
            .Returns(_ =>
            {
                Thread.Sleep(4 * timeoutMs);
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success([]);
            });

        Exception? failure = await Assert.CatchAsync(async () =>
        {
            using JsonRpcResponse response = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = new BlockParameter(2) });
            await Serialize(response);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            test.InnerModule.Received(1).trace_block(Arg.Any<BlockParameter>(), Arg.Any<string?>());
        }
    }

    [Test]
    public async Task trace_filter_from_store_stops_the_stored_window_read_when_the_request_times_out([Values] bool streaming, [Values(1, 2)] int parallelization)
    {
        const int timeoutMs = 50;
        const int windowSize = 64;
        CountingTraceSerializer serializer = new(new ParityLikeTraceSerializer(LimboLogs.Instance), static () => Thread.Sleep(timeoutMs));
        using TestContext test = new(parallelization, streaming, chainLength: windowSize + 1, serializer: serializer, timeoutMs: timeoutMs);
        StoreCallOnEveryBlock(test, serializer);

        Exception? failure = await Assert.CatchAsync(async () =>
        {
            using JsonRpcResponse response = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = BlockParameter.Latest });
            await Serialize(response);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(serializer.DeserializeCount, Is.LessThan(windowSize));
        }
    }

    [Test]
    public async Task trace_filter_from_store_keeps_the_live_trace_block_error([Values] bool streaming)
    {
        using StoreWithUnreadableBlocks test = new(streaming, readableBlocks: 0);
        test.InnerModule.trace_block(Arg.Any<BlockParameter>(), Arg.Any<string?>())
            .Returns(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail("No state available", ErrorCodes.ResourceUnavailable));

        Exception? failure = await Assert.CatchAsync(async () =>
        {
            using JsonRpcResponse response = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = new BlockParameter(1) });
            await Serialize(response);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.InstanceOf<RpcErrorException>());
            Assert.That((failure as RpcErrorException)?.Code, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(failure?.Message, Is.EqualTo("No state available"));
        }
    }

    private static void StoreCallOnEveryBlock(TestContext test, ITraceSerializer<ParityLikeTxTrace> serializer)
    {
        for (ulong number = 1; number <= test.BlockFinder.Head!.Number; number++)
        {
            Hash256 hash = test.BlockFinder.FindHeader(number)!.Hash!;
            test.Store.Set(hash, serializer.Serialize([new ParityLikeTxTrace { BlockHash = hash, TransactionHash = TestItem.KeccakA, Action = Call(TestItem.AddressA, TestItem.AddressB) }]));
        }
    }

    private sealed class StoreWithUnreadableBlocks : IDisposable
    {
        private static readonly byte[] Unreadable = [0];

        private readonly TestContext _context;

        public ITraceRpcModule InnerModule => _context.InnerModule;
        public Hash256[] Hashes { get; }
        public ITraceRpcModule Module => _context.Module;

        public StoreWithUnreadableBlocks(bool streaming, int readableBlocks, int? timeoutMs = null)
        {
            ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
            _context = new TestContext(parallelization: 1, streaming, serializer: new UnreadableTraceSerializer(serializer), timeoutMs: timeoutMs);
            Hashes = [_context.BlockFinder.FindHeader(1)!.Hash!, _context.BlockFinder.FindHeader(2)!.Hash!];
            for (int i = 0; i < Hashes.Length; i++)
            {
                _context.Store.Set(Hashes[i], i < readableBlocks
                    ? serializer.Serialize([new ParityLikeTxTrace { BlockHash = Hashes[i], TransactionHash = TestItem.KeccakA, Action = Call(TestItem.AddressA, TestItem.AddressB) }])
                    : Unreadable);
            }
        }

        public void Dispose() => _context.Dispose();

        private sealed class UnreadableTraceSerializer(ITraceSerializer<ParityLikeTxTrace> inner) : ITraceSerializer<ParityLikeTxTrace>
        {
            public List<ParityLikeTxTrace>? Deserialize(Span<byte> serialized) =>
                serialized.SequenceEqual(Unreadable) ? null : inner.Deserialize(serialized);

            public List<ParityLikeTxTrace>? Deserialize(Stream serialized) => inner.Deserialize(serialized);

            public byte[] Serialize(IReadOnlyCollection<ParityLikeTxTrace> traces) => inner.Serialize(traces);
        }
    }

    private sealed class CountingTraceSerializer(ITraceSerializer<ParityLikeTxTrace> inner, Action? onDeserialize = null) : ITraceSerializer<ParityLikeTxTrace>
    {
        private int _deserializeCount;

        public int DeserializeCount => Volatile.Read(ref _deserializeCount);

        public List<ParityLikeTxTrace>? Deserialize(Span<byte> serialized)
        {
            Interlocked.Increment(ref _deserializeCount);
            onDeserialize?.Invoke();
            return inner.Deserialize(serialized);
        }

        public List<ParityLikeTxTrace>? Deserialize(Stream serialized)
        {
            Interlocked.Increment(ref _deserializeCount);
            onDeserialize?.Invoke();
            return inner.Deserialize(serialized);
        }

        public byte[] Serialize(IReadOnlyCollection<ParityLikeTxTrace> traces) => inner.Serialize(traces);
    }

    private static readonly string StoreAToB = $"{TestItem.AddressA} -> {TestItem.AddressB}";
    private static readonly string StoreBToB = $"{TestItem.AddressB} -> {TestItem.AddressB}";
    private static readonly string StoreAToC = $"{TestItem.AddressA} -> {TestItem.AddressC}";
    private static readonly string StoreRewardToC = $"reward -> {TestItem.AddressC}";

    private static ParityTraceAction Call(Address from, Address to) => new() { Type = "call", CallType = "call", From = from, To = to };

    // A -> B, B -> B, A -> C and a reward to C.
    private static readonly ParityTraceAction[] StoreActions =
    [
        Call(TestItem.AddressA, TestItem.AddressB), Call(TestItem.AddressB, TestItem.AddressB), Call(TestItem.AddressA, TestItem.AddressC),
        new() { Type = "reward", Author = TestItem.AddressC, RewardType = "block" },
    ];

    // Stores the actions (by default StoreActions) at the latest block, then filters them with the given fields.
    private static async Task<string[]> FilterStore(bool streaming, string fields, ParityTraceAction[]? actions = null)
    {
        using TestContext test = new(streaming: streaming);
        Hash256 block = test.DbTrace.BlockHash!;
        test.Store.Set(block, new ParityLikeTraceSerializer(LimboLogs.Instance).Serialize(
            [.. (actions ?? StoreActions).Select(action => new ParityLikeTxTrace
            {
                BlockHash = block,
                TransactionHash = action.Type == "reward" ? null : test.DbTrace.TransactionHash,
                Action = action
            })]));

        // Read as the RPC server reads it, so an omitted or null mode takes the default.
        TraceFilterForRpc filter = JsonSerializer.Deserialize<TraceFilterForRpc>(
            $"{{\"fromBlock\":\"latest\",\"toBlock\":\"latest\"{fields}}}",
            EthereumJsonSerializer.JsonRpcRequestOptions)!;
        using JsonRpcResponse response = test.Module.trace_filter(filter);

        // Written as the RPC server writes it, so the streaming case runs the streamed filter, not the buffered one.
        JToken result = JToken.Parse(Encoding.UTF8.GetString(await Serialize(response)))["result"]!;
        return [.. result.Select(static trace => $"{trace["action"]!["from"] ?? "reward"} -> {trace["action"]!["to"] ?? trace["action"]!["author"] ?? (trace["result"] as JObject)?["address"]}")];
    }

    [Test]
    public async Task trace_filter_from_store_matches_creation_only_by_the_address_its_result_reports(
        [Values("\"intersection\"", "\"union\"")] string mode,
        [Values] bool streaming)
    {
        // A creation by A of B that succeeded, reverted or halted: a failed one still carries B as its action's To.
        ParityTraceAction[] creations =
        [
            new() { Type = "create", CallType = "create", CreationMethod = "create", From = TestItem.AddressA, To = TestItem.AddressB, Result = new() { Address = TestItem.AddressB } },
            new() { Type = "create", CallType = "create", CreationMethod = "create", From = TestItem.AddressA, To = TestItem.AddressB, Result = new() { GasUsed = 0x12 }, Error = "Reverted" },
            new() { Type = "create", CallType = "create", CreationMethod = "create", From = TestItem.AddressA, To = TestItem.AddressB, Result = null, Error = "Out of gas" },
        ];
        string created = $"{TestItem.AddressA} -> {TestItem.AddressB}";
        string failed = $"{TestItem.AddressA} -> ";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await FilterStore(streaming, $",\"toAddress\":[\"{TestItem.AddressB}\"],\"mode\":{mode}", creations), Is.EqualTo(new[] { created }));
            Assert.That(await FilterStore(streaming, $",\"fromAddress\":[\"{TestItem.AddressA}\"],\"toAddress\":[\"{TestItem.AddressB}\"],\"mode\":{mode}", creations),
                Is.EqualTo(mode == "\"union\"" ? new[] { created, failed, failed } : new[] { created }));
        }
    }

    [Test]
    public async Task trace_filter_from_store_combines_address_lists_by_mode(
        [Values(null, "null", "\"intersection\"", "\"union\"")] string? mode,
        [Values] bool streaming)
    {
        string modeField = mode is null ? "" : $",\"mode\":{mode}";
        string[] matched = await FilterStore(streaming, $",\"fromAddress\":[\"{TestItem.AddressA}\"],\"toAddress\":[\"{TestItem.AddressC}\"]{modeField}");
        string[] expected = mode == "\"union\"" ? [StoreAToB, StoreAToC, StoreRewardToC] : [StoreAToC];
        Assert.That(matched, Is.EqualTo(expected));
    }

    [Test]
    public async Task trace_filter_from_store_reads_an_empty_address_list_as_unrestricted(
        [Values(null, "\"intersection\"", "\"union\"")] string? mode,
        [Values] bool streaming)
    {
        string modeField = mode is null ? "" : $",\"mode\":{mode}";
        string[] all = [StoreAToB, StoreBToB, StoreAToC, StoreRewardToC];
        string[] toC = await FilterStore(streaming, $",\"toAddress\":[\"{TestItem.AddressC}\"]{modeField}");
        string[] fromA = await FilterStore(streaming, $",\"fromAddress\":[\"{TestItem.AddressA}\"]{modeField}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(toC, Is.EqualTo(new[] { StoreAToC, StoreRewardToC }));
            Assert.That(fromA, Is.EqualTo(new[] { StoreAToB, StoreAToC }));
            Assert.That(await FilterStore(streaming, $",\"fromAddress\":[],\"toAddress\":[\"{TestItem.AddressC}\"]{modeField}"), Is.EqualTo(toC));
            Assert.That(await FilterStore(streaming, $",\"fromAddress\":[\"{TestItem.AddressA}\"],\"toAddress\":[]{modeField}"), Is.EqualTo(fromA));
            Assert.That(await FilterStore(streaming, $",\"fromAddress\":[],\"toAddress\":[]{modeField}"), Is.EqualTo(all));
            Assert.That(await FilterStore(streaming, $",\"fromAddress\":[]{modeField}"), Is.EqualTo(all));
            Assert.That(await FilterStore(streaming, $",\"toAddress\":[]{modeField}"), Is.EqualTo(all));
        }
    }

    [Test]
    public async Task trace_filter_from_store_reads_after_and_count_as_unsigned(
        [Values("\"0xffffffff\"", "18446744073709551615")] string count, [Values] bool streaming) =>
        Assert.That(await FilterStore(streaming, $",\"after\":1,\"count\":{count}"), Is.EqualTo(new[] { StoreBToB, StoreAToC, StoreRewardToC }));

    [Test]
    public void trace_filter_returns_invalid_params_for_reversed_range([Values(0, 1, 2)] int parallelization)
    {
        using TestContext test = new(parallelization);
        // The omitted fromBlock is the stored head, block 2.
        using ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = test.Module.trace_filter(new TraceFilterForRpc { ToBlock = new BlockParameter(1) });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(result.Result.Error, Is.EqualTo("From block number: 2 is greater than to block number 1"));
        }
        test.InnerModule.DidNotReceive().trace_filter(Arg.Any<TraceFilterForRpc>());
    }

    // The stored head is block 2; an omitted toBlock is latest.
    private static readonly (ulong From, ulong? To)[] RangesPastTheHead = [(3, null), (2, 3), (1, 0xffff)];

    [Test]
    public void trace_filter_returns_invalid_params_for_a_bound_past_the_head(
        [ValueSource(nameof(RangesPastTheHead))] (ulong From, ulong? To) range, [Values(0, 1, 2)] int parallelization)
    {
        using TestContext test = new(parallelization);
        using ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = test.Module.trace_filter(new TraceFilterForRpc
        {
            FromBlock = new BlockParameter(range.From),
            ToBlock = range.To is null ? null : new BlockParameter(range.To.Value)
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(result.Result.Error, Is.EqualTo("requested block range is in the future"));
        }
        test.InnerModule.DidNotReceive().trace_filter(Arg.Any<TraceFilterForRpc>());
    }

    [Test]
    public void trace_filter_returns_from_store_with_the_head_as_a_bound([Values(0, 1, 2)] int parallelization)
    {
        using TestContext test = new(parallelization);

        using ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(2), ToBlock = new BlockParameter(2) });
        using ResultWrapper<IEnumerable<ParityTxTraceFromStore>> expected = ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.DbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace));

        Assert.That(JToken.Parse(Serializer.Serialize(result)), Is.EqualTo(JToken.Parse(Serializer.Serialize(expected))).Using(JToken.EqualityComparer));
        test.InnerModule.DidNotReceive().trace_filter(Arg.Any<TraceFilterForRpc>());
    }

    [Test]
    public void trace_filter_returns_from_inner_module_when_any_block_trace_is_missing([Values(0, 1, 2)] int parallelization)
    {
        using TestContext test = new(parallelization: parallelization);

        Assert.That(JToken.Parse(Serializer.Serialize(test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = BlockParameter.Latest }))), Is.EqualTo(JToken.Parse(Serializer.Serialize(ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(test.NonDbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace))))).Using(JToken.EqualityComparer));

        test.InnerModule.Received().trace_filter(Arg.Any<TraceFilterForRpc>());
    }

    // Pending resolves to the head, whose traces are stored; neither the store nor the inner module may answer it.
    [Test]
    public void rejects_pending_block_as_invalid_params(
        [Values("trace_block", "trace_replayBlockTransactions", "trace_filter fromBlock", "trace_filter toBlock")] string request)
    {
        using TestContext test = new();
        IResultWrapper result = request switch
        {
            "trace_block" => test.Module.trace_block(BlockParameter.Pending),
            "trace_replayBlockTransactions" => test.Module.trace_replayBlockTransactions(BlockParameter.Pending, [ParityTraceTypes.Trace.ToString()]),
            "trace_filter fromBlock" => test.Module.trace_filter(new TraceFilterForRpc { FromBlock = BlockParameter.Pending, ToBlock = BlockParameter.Latest }),
            _ => test.Module.trace_filter(new TraceFilterForRpc { FromBlock = new BlockParameter(1), ToBlock = BlockParameter.Pending }),
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(result.Result.Error, Is.EqualTo("Pending block is not supported for tracing"));
            Assert.That(test.InnerModule.ReceivedCalls(), Is.Empty);
        }
    }

    [Test]
    public async Task trace_filter_from_store_reads_null_member_as_omitted(
        [Values("after", "count")] string member, [Values] bool streaming)
    {
        using TestContext test = new(streaming: streaming);
        test.DbTrace.Action = new ParityTraceAction { Type = "call", CallType = "call", From = TestItem.AddressA, To = TestItem.AddressB };
        test.Store.Set(test.DbTrace.BlockHash!, new ParityLikeTraceSerializer(LimboLogs.Instance).Serialize(new[] { test.DbTrace }));
        const string range = "\"fromBlock\":\"latest\",\"toBlock\":\"latest\"";

        async Task<string> Filter(string json)
        {
            // Read and written as the RPC server does, so the streaming case runs the streamed filter.
            TraceFilterForRpc filter = JsonSerializer.Deserialize<TraceFilterForRpc>(json, EthereumJsonSerializer.JsonRpcRequestOptions)!;
            using JsonRpcResponse response = test.Module.trace_filter(filter);
            return Encoding.UTF8.GetString(await Serialize(response));
        }

        string withNull = await Filter($"{{{range},\"{member}\":null}}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(withNull)["result"]!.Select(static trace => (string?)trace["action"]!["to"]), Is.EqualTo(new[] { TestItem.AddressB.ToString() }), withNull);
            Assert.That(withNull, Is.EqualTo(await Filter($"{{{range}}}")));
        }
        test.InnerModule.DidNotReceive().trace_filter(Arg.Any<TraceFilterForRpc>());
    }

    // Read and written as the RPC server does, so the streaming case runs the streamed filter.
    private static async Task<string> FilterJson(TestContext test, string json)
    {
        TraceFilterForRpc filter = JsonSerializer.Deserialize<TraceFilterForRpc>(json, EthereumJsonSerializer.JsonRpcRequestOptions)!;
        using JsonRpcResponse response = test.Module.trace_filter(filter);
        return Encoding.UTF8.GetString(await Serialize(response));
    }

    [Test]
    public async Task trace_filter_from_store_selects_a_block_by_hash([Values] bool streaming, [Values(0, 1, 2)] int parallelization)
    {
        using TestContext test = new(parallelization, streaming);
        // Below the stored head, block 2.
        BlockHeader block = test.BlockFinder.FindHeader(1)!;
        test.Store.Set(block.Hash!, new ParityLikeTraceSerializer(LimboLogs.Instance).Serialize(
            [.. StoreActions.Select(action => new ParityLikeTxTrace
            {
                BlockHash = block.Hash,
                BlockNumber = block.Number,
                TransactionHash = action.Type == "reward" ? null : TestItem.KeccakB,
                Action = action
            })]));
        string toC = $",\"toAddress\":[\"{TestItem.AddressC}\"],\"after\":1";

        string byHash = await FilterJson(test, $"{{\"blockHash\":\"{block.Hash}\"}}");

        using (Assert.EnterMultipleScope())
        {
            JToken result = JToken.Parse(byHash)["result"]!;
            Assert.That(result.Select(static trace => (string?)trace["blockHash"]), Has.Exactly(StoreActions.Length).EqualTo(block.Hash!.ToString()), byHash);
            Assert.That(byHash, Is.EqualTo(await FilterJson(test, "{\"fromBlock\":\"0x1\",\"toBlock\":\"0x1\"}")));
            Assert.That(await FilterJson(test, $"{{\"blockHash\":\"{block.Hash}\",\"fromBlock\":null,\"toBlock\":null}}"), Is.EqualTo(byHash));
            Assert.That(await FilterJson(test, $"{{\"blockHash\":\"{block.Hash}\"{toC}}}"), Is.EqualTo(await FilterJson(test, $"{{\"fromBlock\":\"0x1\",\"toBlock\":\"0x1\"{toC}}}")));
        }
        test.InnerModule.DidNotReceive().trace_filter(Arg.Any<TraceFilterForRpc>());
    }

    [Test]
    public void block_lookups_report_an_unknown_block_as_not_found([Values] bool replay, [Values("number", "hash", "safe")] string selector)
    {
        using TestContext test = new();
        BlockParameter unknown = selector switch
        {
            "number" => new(test.BlockFinder.Head!.Number + 1),
            "hash" => new(TestItem.KeccakH),
            _ => BlockParameter.Safe,
        };

        int errorCode = replay
            ? test.Module.trace_replayBlockTransactions(unknown, ["trace"]).ErrorCode
            : test.Module.trace_block(unknown).ErrorCode;

        Assert.That(errorCode, Is.EqualTo(ErrorCodes.StandardResourceNotFound));
    }

    [Test]
    public async Task trace_filter_reports_an_unknown_bound_as_not_found(
        [Values("{\"fromBlock\":\"safe\",\"toBlock\":\"latest\"}", "{\"fromBlock\":\"0x1\",\"toBlock\":\"safe\"}")] string filter)
    {
        string response = await FilterJson(new TestContext(), filter);

        Assert.That((int?)JToken.Parse(response)["error"]?["code"], Is.EqualTo(ErrorCodes.StandardResourceNotFound), response);
    }

    [Test]
    public async Task trace_filter_by_hash_returns_an_error_for_an_unknown_or_side_chain_block(
        [Values] bool sideChain, [Values("", ",\"count\":0")] string count, [Values(0, 1, 2)] int parallelization)
    {
        using TestContext test = new(parallelization);
        Hash256 hash = TestItem.KeccakH;
        if (sideChain)
        {
            // A sibling of the stored head whose traces are stored too: known, but not canonical.
            Block sibling = Build.A.Block.WithParent(test.BlockFinder.FindHeader(1)!).WithExtraData([1]).TestObject;
            test.BlockFinder.SuggestBlock(sibling, BlockTreeSuggestOptions.ForceDontSetAsMain);
            test.Store.Set(sibling.Hash!, new ParityLikeTraceSerializer(LimboLogs.Instance).Serialize(test.DbTraces));
            hash = sibling.Hash!;
        }

        string response = await FilterJson(test, $"{{\"blockHash\":\"{hash}\"{count}}}");

        JToken? error = JToken.Parse(response)["error"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)error?["code"], Is.EqualTo(ErrorCodes.StandardResourceNotFound), response);
            Assert.That((string?)error?["message"], Is.EqualTo(sideChain ? $"{hash} block is not canonical" : "header not found"), response);
        }
        test.InnerModule.DidNotReceive().trace_filter(Arg.Any<TraceFilterForRpc>());
    }

    [Test]
    public async Task trace_block_to_async_stream()
    {
        using TestContext test = new();

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = test.Module.trace_block(BlockParameter.Latest);
        Assert.That(result.Data, Is.AssignableTo<IStreamableResult>());
        IStreamableResult streaming = (IStreamableResult)result.Data;

        await using AsyncCompletingStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream);

        await Assert.DoesNotThrowAsync(async () => await streaming.WriteToAsync(writer, CancellationToken.None));

        await writer.CompleteAsync();
    }

    private sealed class TestContext : IDisposable
    {
        private readonly IContainer _container;

        public ParityLikeTxTrace DbTrace { get; }
        public ParityLikeTxTrace[] DbTraces { get; }
        public ParityLikeTxTrace[] NonDbTraces { get; }
        public ITraceRpcModule InnerModule { get; }
        public MemDb Store { get; }
        public IBlockTree BlockFinder { get; }
        public IReceiptFinder ReceiptFinder { get; }
        public ITraceRpcModule Module { get; }

        public TestContext(int parallelization = 0, bool streaming = true, ParityTraceTypes storedTraceTypes = ParityTraceTypes.All,
            ulong chainLength = 3, ITraceSerializer<ParityLikeTxTrace>? serializer = null, int? timeoutMs = null)
        {
            InnerModule = Substitute.For<ITraceRpcModule>();
            Store = new MemDb();
            ReceiptFinder = Substitute.For<IReceiptFinder>();
            ParityLikeTraceSerializer storeSerializer = new(LimboLogs.Instance);
            TraceStoreConfig traceConfig = new() { Enabled = true, BlocksToKeep = 0, TraceTypes = storedTraceTypes, DeserializationParallelization = parallelization };
            JsonRpcConfig rpcConfig = new() { EnableTracingStreamMode = streaming };
            if (timeoutMs is { } timeout) rpcConfig.Timeout = timeout;
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(traceConfig, rpcConfig))
                .AddModule(new TraceStorePlugin(traceConfig, rpcConfig).Module)
                .AddSingleton<ITraceRpcModule>(InnerModule)
                .AddKeyedSingleton<IDb>(TraceStorePlugin.DbName, Store)
                .AddKeyedSingleton<IReceiptFinder>(IReceiptFinder.RegenerableKey, ReceiptFinder)
                .AddSingleton<ITraceSerializer<ParityLikeTxTrace>>(serializer ?? storeSerializer)
                .Build();
            BlockFinder = _container.Resolve<IBlockTree>();
            AddChain(BlockFinder, chainLength);
            Module = _container.Resolve<ITraceRpcModule>();
            Hash256 dbTransaction = Build.A.Transaction.TestObject.Hash!;
            Hash256 dbBlock = BlockFinder.Head!.Hash!;
            DbTrace = new() { BlockHash = dbBlock, TransactionHash = dbTransaction };
            DbTraces = new[] { DbTrace };
            Hash256 nonDbTransaction = TestItem.KeccakA;
            NonDbTraces = new[] { new ParityLikeTxTrace() { BlockHash = dbBlock, TransactionHash = nonDbTransaction } };
            Store.Set(dbBlock, storeSerializer.Serialize(DbTraces));
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
                .Returns(ResultWrapper<ParityTxTraceFromReplay?>.Success(new ParityTxTraceFromReplay(NonDbTraces[0])));

            InnerModule.trace_replayBlockTransactions(Arg.Any<BlockParameter>(), Arg.Any<string[]>())
                .Returns(nonDbReplaysWrapper);

            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> nonDbFromStoreWrapper = ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success(NonDbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace));
            InnerModule.trace_filter(Arg.Any<TraceFilterForRpc>())
                .Returns(nonDbFromStoreWrapper);

            InnerModule.trace_block(BlockParameter.Latest)
                .Returns(nonDbFromStoreWrapper);

            InnerModule.trace_transaction(nonDbTransaction)
                .Returns(ResultWrapper<IEnumerable<ParityTxTraceFromStore>?>.Success(NonDbTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace)));
        }

        public void Dispose() => _container.Dispose();

        private static void AddChain(IBlockTree blockTree, ulong length)
        {
            Block genesis = Build.A.Block.Genesis.TestObject;
            genesis.Header.TotalDifficulty = genesis.Header.Difficulty;
            blockTree.SuggestBlock(genesis);
            blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, preloadedBlocks: [genesis]);
            BlockTreeBuilder.ExtendTree(blockTree, length);
        }
    }
}
