// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [TestCase(false, TestName = "Debug_traceTransaction_WithDeepStack_StreamsInBoundedChunks")]
    [TestCase(true, TestName = "Debug_traceTransaction_WithDeepStackAndGrowingMemory_StreamsInBoundedChunks")]
    public async Task Debug_traceTransaction_WithLargeStructLogs_StreamsInBoundedChunks(bool enableMemory)
    {
        // Scenario:
        // 1. A contract creation fills the stack 512 deep, then grows memory in a loop until it runs out of gas.
        // 2. Every struct log carries the whole stack (and memory), so each entry is tens of KB.
        // 3. The response goes through the real response writer into a writer that records the bytes between flushes.
        const long maxUnflushedBytes = 2 * 1024 * 1024;
        using Context context = await Context.Create();
        Transaction transaction = Build.A.Transaction
            .WithNonce(context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithCode(DeepStackLoopCode.Build(stackDepth: 512, memoryStride: 256))
            .WithGasLimit(80_000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        await context.Blockchain.AddBlock(transaction);

        FlushRecordingPipeWriter pipe = new(keepOutput: true);
        await RpcTest.TestStreamedRequest(context.DebugRpcModule, pipe, "debug_traceTransaction",
            transaction.Hash, new GethTraceOptions { EnableMemory = enableMemory });

        using JsonDocument document = JsonDocument.Parse(pipe.WrittenSpan.ToArray());
        JsonElement result = document.RootElement.GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("failed").GetBoolean(), Is.True, "precondition: the loop ends out of gas");
            Assert.That(result.GetProperty("structLogs").GetArrayLength(), Is.GreaterThan(1024), "precondition: the whole loop is traced");
            Assert.That(pipe.TotalBytes, Is.GreaterThan(4 * maxUnflushedBytes), "precondition: the response must span many flush windows");
            Assert.That(pipe.MaxUnflushedBytes, Is.LessThanOrEqualTo(maxUnflushedBytes),
                "the response writer must see a flush every megabyte or so, however large each struct log is");
        }
    }

    [Test]
    public async Task Debug_traceBlockByNumber_WithManyMidSizedTransactions_StreamsInBoundedChunks()
    {
        // Scenario:
        // 1. A block carries eight contract creations, each looping until out of gas with a stack 32 deep.
        // 2. Each transaction has a few thousand struct logs, well under 8192, but the block output is tens of MB.
        // 3. The response goes through the real response writer into a writer that records the bytes between flushes.
        const int transactionCount = 8;
        const long maxUnflushedBytes = 2 * 1024 * 1024;
        using Context context = await Context.Create();
        ulong nonce = context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction[] transactions = new Transaction[transactionCount];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithNonce(nonce + (ulong)i)
                .WithCode(DeepStackLoopCode.Build(stackDepth: 32, memoryStride: 32))
                .WithGasLimit(80_000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;
        }
        await context.Blockchain.AddBlock(transactions);

        FlushRecordingPipeWriter pipe = new(keepOutput: true);
        await RpcTest.TestStreamedRequest(context.DebugRpcModule, pipe, "debug_traceBlockByNumber",
            context.Blockchain.BlockTree.Head!.Number, new GethTraceOptions());

        using JsonDocument document = JsonDocument.Parse(pipe.WrittenSpan.ToArray());
        JsonElement[] traces = [.. document.RootElement.GetProperty("result").EnumerateArray()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces, Has.Length.EqualTo(transactionCount), "precondition: every transaction of the block is traced");
            Assert.That(traces.Select(static t => t.GetProperty("result").GetProperty("structLogs").GetArrayLength()),
                Is.All.InRange(1024, 8191), "precondition: each transaction is large but under 8192 struct logs");
            Assert.That(pipe.TotalBytes, Is.GreaterThan(8 * maxUnflushedBytes), "precondition: the block response must span many flush windows");
            Assert.That(pipe.MaxUnflushedBytes, Is.LessThanOrEqualTo(maxUnflushedBytes),
                "the flush window must carry across transaction boundaries, so a block of mid-sized transactions is not held whole");
        }
    }

    [Test]
    public async Task GethLikeTxTraceStreamingSingleResult_WhenCancelledMidTrace_PropagatesCancellation()
    {
        using CancellationTokenSource requestCts = new();
        CancellationTokenSource timeoutCts = new();

        using GethLikeTxTraceStreamingSingleResult result = new(
            (writer, _, ct) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("pc"u8);
                writer.WriteNumberValue(0);
                writer.WriteEndObject();

                requestCts.Cancel();
                ct.ThrowIfCancellationRequested();
                return null;
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        Pipe pipe = new();

        Assert.That(async () => await result.WriteToAsync(pipe.Writer, requestCts.Token), Throws.InstanceOf<OperationCanceledException>(),
            "the response writer must distinguish cancellation from successful completion");

        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        Action parse = () => JToken.Parse(body);
        Assert.That(parse, Throws.Nothing, "the finally block must close the structLogs array and the outer object even when cancellation fires");

        JToken parsed = JToken.Parse(body);
        Assert.That(parsed["failed"]!.Value<bool>(), Is.True, "a cancelled mid-trace is reported as failed");
    }

    [Test]
    public void StructLogEnvelopeWriter_BundleDefault_RetainsDurationError()
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        StructLogEnvelopeWriter.EmitTraceObject(writer, null, CancellationToken.None,
            (_, _, _) => throw new TraceDurationFormatException("bad duration"),
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>(), fallbackGas: 21_000);
        writer.Flush();
        JToken parsed = JToken.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed["failed"]!.Value<bool>(), Is.True);
            Assert.That(parsed["error"]!.Value<string>(), Is.EqualTo("tracing failed: bad duration"));
            Assert.That(parsed["errorCode"]!.Value<int>(), Is.EqualTo(ErrorCodes.InternalError));
            Assert.That(parsed["gas"]!.Value<int>(), Is.EqualTo(21_000));
        }
    }

    [Test]
    public void GethLikeTxTraceStreamingSingleResult_WhenTraceThrows_WritesErrorAndErrorCode()
    {
        CancellationTokenSource timeoutCts = new();

        using GethLikeTxTraceStreamingSingleResult result = new(
            (writer, _, _) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("pc"u8);
                writer.WriteNumberValue(0);
                writer.WriteEndObject();

                throw new InvalidOperationException("simulated mid-trace failure");
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        ArrayBufferWriter<byte> bufferWriter = new();
        using Utf8JsonWriter writer = new(bufferWriter);
        result.WriteAsJson(writer);
        writer.Flush();

        JToken parsed = JToken.Parse(Encoding.UTF8.GetString(bufferWriter.WrittenSpan));
        Assert.That(parsed["failed"]!.Value<bool>(), Is.True);
        Assert.That(parsed["error"]!.Value<string>(), Does.Contain("simulated mid-trace failure"));
        Assert.That(parsed["errorCode"]!.Value<int>(), Is.EqualTo(ErrorCodes.InternalError), "generic exceptions map to InternalError; InsufficientBalanceException maps to InvalidInput");
    }

    [Test]
    public void GethLikeBlockEnvelopeStreamingTracer_WhenDisposedMidTx_EmitsTxHashAndClosesEnvelope()
    {
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;

        ArrayBufferWriter<byte> outerBuffer = new();
        using Utf8JsonWriter jsonWriter = new(outerBuffer);

        jsonWriter.WriteStartArray();

        using (GethLikeBlockEnvelopeStreamingTracer tracer = new(GethTraceOptions.Default, jsonWriter, pipeWriter: null, CancellationToken.None))
        {
            ((Evm.Tracing.IBlockTracer)tracer).StartNewTxTrace(tx);
            // simulate a mid-tx failure: no MarkAsSuccess/MarkAsFailed is called, OnEnd never runs.
        }

        jsonWriter.WriteEndArray();
        jsonWriter.Flush();

        JArray result = (JArray)JToken.Parse(Encoding.UTF8.GetString(outerBuffer.WrittenSpan));
        Assert.That(result, Has.Count.EqualTo(1), "Dispose must seal the in-flight per-tx envelope");

        JToken entry = result[0]!;
        Assert.That((bool)entry["result"]!["failed"]!, Is.True);
        Assert.That((string)entry["txHash"]!, Is.EqualTo(tx.Hash!.ToString()).IgnoreCase, "txHash must be present on every entry, including those closed by Dispose");
    }

    [Test]
    public void GethLikeBlockEnvelopeStreamingTracer_WhenDisposeFailsToCloseEnvelope_DoesNotRetryOnSecondDispose()
    {
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;

        ArrayBufferWriter<byte> outerBuffer = new();
        using Utf8JsonWriter jsonWriter = new(outerBuffer);
        GethLikeBlockEnvelopeStreamingTracer tracer = new(GethTraceOptions.Default, jsonWriter, pipeWriter: null, CancellationToken.None);

        jsonWriter.WriteStartArray();
        ((Evm.Tracing.IBlockTracer)tracer).StartNewTxTrace(tx);
        jsonWriter.WriteStartObject();

        Action firstDispose = tracer.Dispose;
        Assert.That(firstDispose, Throws.TypeOf<InvalidOperationException>());

        Action secondDispose = tracer.Dispose;
        Assert.That(secondDispose, Throws.Nothing, "Dispose should not attempt to close the same malformed envelope twice");
    }

    [Test]
    public async Task GethLikeTxTraceStreamingBlockResult_WhenCancelledMidBlock_PropagatesCancellation()
    {
        using CancellationTokenSource requestCts = new();
        CancellationTokenSource timeoutCts = new();

        using GethLikeTxTraceStreamingBlockResult result = new(
            (writer, _, ct) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("result"u8);
                writer.WriteStartObject();
                writer.WriteEndObject();
                writer.WritePropertyName("txHash"u8);
                writer.WriteStringValue(TestItem.KeccakA.ToString());
                writer.WriteEndObject();

                requestCts.Cancel();
                ct.ThrowIfCancellationRequested();
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        Pipe pipe = new();

        Assert.That(async () => await result.WriteToAsync(pipe.Writer, requestCts.Token), Throws.InstanceOf<OperationCanceledException>(),
            "the response writer must distinguish cancellation from successful completion");

        await pipe.Writer.CompleteAsync();
        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        Action parse = () => JToken.Parse(body);
        Assert.That(parse, Throws.Nothing, "the finally block must close the outer array even when cancellation fires");

        JArray parsed = (JArray)JToken.Parse(body);
        Assert.That(parsed, Has.Count.EqualTo(1), "the one entry written before cancellation must be present in a well-formed array");
    }

    [Test]
    public async Task GethLikeTxTraceStreamingBlockResult_WhenTraceThrows_EmitsSentinelErrorEntry()
    {
        CancellationTokenSource timeoutCts = new();

        using GethLikeTxTraceStreamingBlockResult result = new(
            (writer, _, _) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("result"u8);
                writer.WriteStartObject();
                writer.WriteEndObject();
                writer.WritePropertyName("txHash"u8);
                writer.WriteStringValue(TestItem.KeccakA.ToString());
                writer.WriteEndObject();

                throw new InvalidOperationException("simulated mid-block failure");
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        Pipe pipe = new();
        await result.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        JArray parsed = (JArray)JToken.Parse(body);
        Assert.That(parsed, Has.Count.EqualTo(2), "the array must contain the one good entry plus a sentinel error entry so clients can detect the mid-block failure");

        JToken sentinel = parsed[1]!;
        Assert.That((string)sentinel["error"]!, Does.Contain("simulated mid-block failure"), "the sentinel must carry the human-readable failure reason");
        Assert.That((int)sentinel["errorCode"]!, Is.EqualTo(ErrorCodes.InternalError), "the sentinel must carry the Geth-equivalent errorCode (generic exceptions map to InternalError)");
    }

    [Test]
    public async Task GethLikeBlockNativeStreamingTracer_WhenTracerFailsMidBlock_KeepsWrittenEntriesAndEmitsSentinel()
    {
        Transaction first = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction second = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithTransactions(first, second).TestObject;

        using GethLikeTxTraceStreamingBlockResult result = new(
            (writer, pipeWriter, ct) =>
            {
                IBlockTracer tracer = new GethLikeBlockNativeStreamingTracer(
                    (_, tx) => tx == first ? new NativeNoopTracer(tx, GethTraceOptions.Default) : throw new InvalidOperationException("simulated tracer failure"),
                    writer, pipeWriter, ct);
                tracer.StartNewBlockTrace(block);
                foreach (Transaction tx in block.Transactions)
                {
                    tracer.StartNewTxTrace(tx);
                    tracer.EndTxTrace();
                }
            },
            new CancellationTokenSource(),
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        Pipe pipe = new();
        await result.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();
        ReadResult readResult = await pipe.Reader.ReadAsync();

        JArray parsed = JArray.Parse(Encoding.UTF8.GetString(readResult.Buffer.ToArray()));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Has.Count.EqualTo(2));
            Assert.That((string)parsed[0]["txHash"]!, Is.EqualTo(first.Hash!.ToString()), "the transaction traced before the failure is already written");
            Assert.That(parsed[0]["result"], Is.Not.Null);
            Assert.That((string)parsed[1]["error"]!, Does.Contain("simulated tracer failure"));
        }
    }

    [Test]
    public void GethLikeBlockNativeStreamingTracer_WhenBatchOutputFails_DisposesEveryTraceOnce(
        [Values] bool cancel, [Values] bool collectionOwnsTraces)
    {
        using CancellationTokenSource cancellation = new();
        BatchTraceDisposalCounter[] counters = [new(), new(), new(), new()];
        GethLikeTxTrace[] entries = [.. counters.Select(static counter => new GethLikeTxTrace(counter) { TxHash = TestItem.KeccakA })];
        IReadOnlyCollection<GethLikeTxTrace> traces;
        if (collectionOwnsTraces)
        {
            DisposableResettableList<GethLikeTxTrace> owner = [];
            owner.AddRange(entries);
            traces = owner;
        }
        else
        {
            traces = entries;
        }
        BatchFailurePipeWriter pipe = new(cancellation, cancel, counters);
        using Utf8JsonWriter writer = new(pipe);
        writer.WriteStartArray();

        Action write = () => GethLikeBlockNativeStreamingTracer.WriteBatch(writer, pipe, traces, cancellation.Token);

        if (cancel) Assert.That(write, Throws.InstanceOf<OperationCanceledException>(), "cancellation must escape instead of returning a successful truncated batch");
        else Assert.That(write, Throws.InvalidOperationException.With.Message.EqualTo("simulated batch output failure"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pipe.Flushes, Is.EqualTo(2), "the failure follows one completed transaction entry");
            Assert.That(counters.Select(static counter => counter.Disposals), Is.All.EqualTo(1),
                "written, current and unvisited traces must all release their resources exactly once");
        }
    }

    private sealed class BatchTraceDisposalCounter : IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose() => Disposals++;
    }

    private sealed class BatchFailurePipeWriter(CancellationTokenSource cancellation, bool cancel, BatchTraceDisposalCounter[] counters) : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public int Flushes { get; private set; }

        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Assert.That(counters[Flushes].Disposals, Is.EqualTo(1), "the current trace must be released before output backpressure can block");
            if (++Flushes == 2)
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw new InvalidOperationException("simulated batch output failure");
            }
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
        }
    }
}
