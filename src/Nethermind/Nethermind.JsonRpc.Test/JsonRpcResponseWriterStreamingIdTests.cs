// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Core.Memory;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// Locks in that the streamable JSON-RPC response envelope written by
/// <see cref="JsonRpcResponseWriter.WriteAsync"/> picks up the branch's
/// raw-decimal / int64 / string / null ID handling. Regression guard for
/// future streaming-result implementations.
/// </summary>
[TestFixture]
public class JsonRpcResponseWriterStreamingIdTests
{
    private readonly GCKeeper _keeper = new(NoGCStrategy.Instance, NullLogManager.Instance);

    [OneTimeTearDown]
    public void DisposeKeeper() => _keeper.Dispose();

    private JsonRpcService.StreamingContext CreateStreamingContext() => new(
        new JsonRpcService(NullModuleProvider.Instance, NullLogManager.Instance, new JsonRpcConfig(), _keeper),
        new JsonRpcRequest { Id = new JsonRpcId(42L), Method = "trace_call" }, "trace_call");

    private sealed class StubStreamable : IStreamableResult
    {
        public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("\"ok\""u8);
            return ValueTask.CompletedTask;
        }
    }

    public static IEnumerable<TestCaseData> IdCases()
    {
        yield return new TestCaseData(new JsonRpcId(42L), "42").SetName("Int64Id_UnquotedNumber");
        // Value beyond Int64.MaxValue exercises the decimal branch of WriteIdRaw.
        yield return new TestCaseData(new JsonRpcId(9876543210987654321m), "9876543210987654321").SetName("DecimalId_RawPreserved");
        yield return new TestCaseData(new JsonRpcId("abc"), "\"abc\"").SetName("StringId_Quoted");
        yield return new TestCaseData(JsonRpcId.Null, "null").SetName("NullId_AsJsonNull");
        yield return new TestCaseData(JsonRpcId.Missing, "null").SetName("MissingId_AsJsonNull");
    }

    [TestCaseSource(nameof(IdCases))]
    public async Task Streaming_envelope_serializes_id_correctly(JsonRpcId id, string serializedId)
    {
        Pipe pipe = new();
        using JsonRpcSuccessResponse response = new() { Id = id, Result = new StubStreamable() };

        await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        System.IO.Pipelines.ReadResult read = await pipe.Reader.ReadAsync();
        string envelope = Encoding.UTF8.GetString(read.Buffer.ToArray());
        await pipe.Reader.CompleteAsync();

        Assert.That(envelope, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"ok\",\"id\":{serializedId}}}"));
    }

    [Test]
    public async Task Validation_failure_replaces_only_uncommitted_response(
        [Values(0, 1, 2)] int commitMode, [Values] bool rpcError)
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        Exception failure = rpcError ? new RpcErrorException(ErrorCodes.ResourceNotFound, "transaction not found")
            : new InsufficientBalanceException(TestItem.AddressA);
        using JsonRpcSuccessResponse response = CreateInvalidTransactionResponse(commitMode, failure);

        if (commitMode != 2)
        {
            await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        }
        else
        {
            Exception? thrown = await Assert.CatchAsync(async () =>
                await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None));
            Assert.That(thrown, Is.SameAs(failure));
        }
        await pipe.Writer.CompleteAsync();
        ReadResult read = await pipe.Reader.ReadAsync();
        string envelope = Encoding.UTF8.GetString(read.Buffer.ToArray());
        await pipe.Reader.CompleteAsync();

        if (commitMode != 2)
        {
            using JsonDocument document = JsonDocument.Parse(envelope);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(document.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(42));
                Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
                    Is.EqualTo(rpcError ? ErrorCodes.ResourceNotFound : ErrorCodes.InvalidInput));
                if (rpcError) Assert.That(document.RootElement.GetProperty("error").GetProperty("message").GetString(), Is.EqualTo("transaction not found"));
                Assert.That(document.RootElement.TryGetProperty("result", out _), Is.False);
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(envelope, Does.StartWith("{\"jsonrpc\":\"2.0\",\"result\":"));
                Assert.That(envelope, Does.Not.Contain("\"error\""));
            }
        }
    }

    [Test]
    public void Module_selected_error_keeps_its_code_without_method_identity([Values] bool reflectionWrapped)
    {
        RpcErrorException failure = new(ErrorCodes.InvalidParams, "explicit failure");
        Exception exception = reflectionWrapped ? new System.Reflection.TargetInvocationException(failure) : failure;
        using JsonRpcErrorResponse response = CreateStreamingContext().MapException(exception);
        Assert.That(response.Error, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Error!.Code, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(response.Error!.Message, Is.EqualTo("explicit failure"));
            Assert.That(response.Error!.Data, Is.Null);
        }
    }

    [Test]
    public async Task Deferred_single_trace_error_preserves_previous_batch_response(
        [Values] bool isBatch, [Values] bool rpcError)
    {
        using RecyclableMemoryStream stream = RecyclableStream.GetStream("test");
        RewindableStreamPipeWriter transport = new(stream, initialWrittenCount: 1234);
        using JsonRpcSuccessResponse previous = new() { Id = new JsonRpcId("previous"), Result = new StubStreamable() };
        using JsonRpcSuccessResponse response = new()
        {
            Id = new JsonRpcId(42L),
            Result = new GethLikeTxTraceStreamingSingleResult((writer, _, _) =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("pc", 0);
                writer.WriteEndObject();
                writer.Flush();
                throw rpcError ? new RpcErrorException(ErrorCodes.ResourceNotFound, "transaction not found")
                    : new InvalidOperationException("host failure");
            }, new CancellationTokenSource(), NullLogger.Instance),
            Streaming = CreateStreamingContext()
        };
        try
        {
            if (isBatch)
            {
                JsonRpcResponseWriter.WriteBatchStart(transport);
                await JsonRpcResponseWriter.WriteAsync(transport, previous, EthereumJsonSerializer.JsonOptions,
                    isBatch: true, CancellationToken.None);
                JsonRpcResponseWriter.WriteBatchSeparator(transport);
            }
            byte[] prefix = stream.ToArray();
            await JsonRpcResponseWriter.WriteAsync(transport, response, EthereumJsonSerializer.JsonOptions,
                isBatch, CancellationToken.None);
            if (isBatch) JsonRpcResponseWriter.WriteBatchEnd(transport);
            await transport.CompleteAsync();
            byte[] json = stream.ToArray();
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement item = document.RootElement;
            if (isBatch)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(item.ValueKind, Is.EqualTo(JsonValueKind.Array));
                    Assert.That(item.GetArrayLength(), Is.EqualTo(2));
                }
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(json.AsSpan().StartsWith(prefix), Is.True);
                    Assert.That(item[0].GetProperty("id").GetString(), Is.EqualTo("previous"));
                    Assert.That(item[0].GetProperty("result").GetString(), Is.EqualTo("ok"));
                }
                item = item[1];
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(item.GetProperty("jsonrpc").GetString(), Is.EqualTo("2.0"));
                Assert.That(item.GetProperty("id").GetInt32(), Is.EqualTo(42));
                Assert.That(item.TryGetProperty("_streamStatus", out _), Is.False);
                Assert.That(transport.WrittenCount, Is.EqualTo(1234 + stream.Length));
                if (rpcError)
                {
                    Assert.That(item.TryGetProperty("result", out _), Is.False);
                    Assert.That(item.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.ResourceNotFound));
                    Assert.That(item.GetProperty("error").GetProperty("message").GetString(), Is.EqualTo("transaction not found"));
                    Assert.That(item.GetProperty("error").TryGetProperty("data", out _), Is.False);
                }
                else
                {
                    Assert.That(item.TryGetProperty("error", out _), Is.False);
                    Assert.That(item.GetProperty("result").GetProperty("failed").GetBoolean(), Is.True);
                    Assert.That(item.GetProperty("result").GetProperty("errorCode").GetInt32(), Is.EqualTo(ErrorCodes.InternalError));
                    Assert.That(item.GetProperty("result").GetProperty("error").GetString(), Is.EqualTo("tracing failed: host failure"));
                    Assert.That(item.GetProperty("result").GetProperty("structLogs")[0].GetProperty("pc").GetInt32(), Is.Zero);
                }
            }
        }
        finally
        {
            await transport.CompleteAsync();
        }
    }

    [Test]
    public async Task Buffered_failure_rewinds_to_current_response(
        [Values(0, 1, 2)] int commitMode, [Values(0, 1234)] int initialWrittenCount)
    {
        using RecyclableMemoryStream stream = RecyclableStream.GetStream("test");
        RewindableStreamPipeWriter transport = new(stream, initialWrittenCount: initialWrittenCount);
        transport.Write("[1,"u8);
        using JsonRpcSuccessResponse response = CreateInvalidTransactionResponse(commitMode);

        await JsonRpcResponseWriter.WriteAsync(transport, response, EthereumJsonSerializer.JsonOptions,
            isBatch: true, CancellationToken.None);
        transport.Write("]"u8);
        await transport.CompleteAsync();

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement[0].GetInt32(), Is.EqualTo(1));
            Assert.That(document.RootElement[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(transport.WrittenCount, Is.EqualTo(initialWrittenCount + stream.Length));
        }
    }

    [Test]
    public async Task Pre_cancelled_direct_write_is_cancelled_without_output()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        using JsonRpcSuccessResponse response = new() { Result = new ChunkedResult() };
        Pipe pipe = new();
        ValueTask write = JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, cancellation.Token);

        Assert.That(write.IsCanceled, Is.True, "a cancelled write must not look like a failure to task-based callers");
        Assert.That(async () => await write, Throws.TypeOf<OperationCanceledException>());
        await pipe.Writer.CompleteAsync();
        ReadResult read = await pipe.Reader.ReadAsync();
        Assert.That(read.Buffer.IsEmpty, Is.True);
        await pipe.Reader.CompleteAsync();
    }

    [Test]
    public async Task Stream_failure_propagates_without_handler_or_after_transport_cancellation(
        [Values] bool cancelTransport, [Values] bool rpcError)
    {
        using CancellationTokenSource cancellation = new();
        Exception failure = rpcError ? new RpcErrorException(ErrorCodes.ResourceNotFound, "transaction not found")
            : new InsufficientBalanceException(TestItem.AddressA);
        using JsonRpcSuccessResponse response = new()
        {
            Result = new InvalidTransactionResult(0, cancelTransport ? cancellation.Cancel : null, failure),
            Streaming = cancelTransport ? CreateStreamingContext() : null
        };
        Pipe pipe = new();
        try
        {
            Exception? thrown = await Assert.CatchAsync(async () =>
                await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, cancellation.Token));
            Assert.That(thrown, Is.SameAs(failure));
            await pipe.Writer.CompleteAsync();
            ReadResult read = await pipe.Reader.ReadAsync();
            Assert.That(read.Buffer.IsEmpty, Is.EqualTo(cancelTransport));
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Trace_transport_cancellation_does_not_complete_success([Values(0, 20_000)] int padding)
    {
        using CancellationTokenSource transport = new();
        using CancellationTokenSource timeout = new();
        using JsonRpcSuccessResponse response = new()
        {
            Id = new JsonRpcId(42L),
            Result = new ParityTxTraceStreamingResult<int>((writer, _, ct) =>
            {
                writer.WriteStringValue(new string('x', padding));
                writer.Flush();
                transport.Cancel();
                ct.ThrowIfCancellationRequested();
            }, timeout, LimboLogs.Instance.GetClassLogger<JsonRpcResponseWriterStreamingIdTests>()),
            Streaming = CreateStreamingContext()
        };
        response.Streaming!.ReportCompletion = true;
        long successes = Metrics.JsonRpcSuccesses;
        long errors = Metrics.JsonRpcErrors;
        using MemoryStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, transport.Token));
            await writer.CompleteAsync();
            string envelope = Encoding.UTF8.GetString(stream.ToArray());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Metrics.JsonRpcSuccesses, Is.EqualTo(successes));
                Assert.That(Metrics.JsonRpcErrors, Is.EqualTo(errors));
                Assert.That(envelope, Does.Not.Contain("\"error\""));
                Assert.That(envelope, Does.Not.Contain("\"id\":42"));
                if (padding == 0) Assert.That(envelope, Is.Empty);
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    [Test]
    public async Task Expired_trace_timeout_does_not_start_execution()
    {
        using CancellationTokenSource timeout = new();
        timeout.Cancel();
        using ParityTxTraceStreamingResult<int> result = new(
            (_, _, _) => throw new AssertionException("Expired requests must not execute"),
            timeout, LimboLogs.Instance.GetClassLogger<JsonRpcResponseWriterStreamingIdTests>());
        using MemoryStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await result.WriteToAsync(writer, CancellationToken.None));
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    [Test]
    public async Task Transport_write_failure_is_not_mapped_to_an_rpc_error()
    {
        using JsonRpcSuccessResponse response = new()
        {
            Result = new InvalidTransactionResult(2),
            Streaming = CreateStreamingContext()
        };
        await Assert.ThrowsAsync<IOException>(async () =>
            await JsonRpcResponseWriter.WriteAsync(new FailingPipeWriter(), response, EthereumJsonSerializer.JsonOptions, CancellationToken.None));
    }

    [Test]
    [NonParallelizable]
    public async Task Deferred_failure_obeys_the_exact_prefix_limit([Values(16383, 16384, 16385)] int prefixBytes)
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        using JsonRpcSuccessResponse response = new()
        {
            Id = 42,
            Result = new PrefixFailureResult(prefixBytes),
            Streaming = CreateStreamingContext()
        };
        response.Streaming.ReportCompletion = true;
        long errors = Metrics.JsonRpcErrors;
        long successes = Metrics.JsonRpcSuccesses;
        if (prefixBytes <= 16384)
            await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        else
            await Assert.ThrowsAsync<InsufficientBalanceException>(async () =>
                await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None));
        await pipe.Writer.CompleteAsync();
        ReadResult read = await pipe.Reader.ReadAsync();
        if (prefixBytes <= 16384)
        {
            using JsonDocument document = JsonDocument.Parse(read.Buffer);
            Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidInput));
        }
        else Assert.That(read.Buffer.Length, Is.EqualTo(prefixBytes));
        await pipe.Reader.CompleteAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.JsonRpcErrors - errors, Is.EqualTo(1));
            Assert.That(Metrics.JsonRpcSuccesses, Is.EqualTo(successes));
        }
    }

    private sealed class PrefixFailureResult(int prefixBytes) : IStreamableResult
    {
        public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write(new byte[prefixBytes - "{\"jsonrpc\":\"2.0\",\"result\":"u8.Length]);
            throw new InsufficientBalanceException(TestItem.AddressA);
        }
    }

    private sealed class FailingPipeWriter : PipeWriter
    {
        public override Memory<byte> GetMemory(int sizeHint = 0) => throw new IOException("Transport failed");
        public override Span<byte> GetSpan(int sizeHint = 0) => throw new IOException("Transport failed");
        public override void Advance(int bytes) => throw new NotSupportedException();
        public override void CancelPendingFlush() => throw new NotSupportedException();
        public override void Complete(Exception? exception = null) => throw new NotSupportedException();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Test]
    public async Task Streaming_envelope_preserves_flush_threshold()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        FlushCountingPipeWriter transport = new(pipe.Writer);
        using JsonRpcSuccessResponse response = new() { Result = new ChunkedResult() };
        await JsonRpcResponseWriter.WriteAsync(new CountingPipeWriter(transport), response,
            EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        await transport.CompleteAsync();
        await pipe.Reader.CompleteAsync();

        Assert.That(transport.FlushCount, Is.EqualTo(2));
    }

    [Test]
    public async Task Buffered_response_preserves_preceding_batch_byte_count([Values] bool bufferResponse)
    {
        using RecyclableMemoryStream stream = RecyclableStream.GetStream("test");
        CountingWriter transport = bufferResponse
            ? new RewindableStreamPipeWriter(stream, initialWrittenCount: 1234)
            : new CountingStreamPipeWriter(stream, initialWrittenCount: 1234);
        CountingResult result = new();
        using JsonRpcSuccessResponse response = new() { Result = result };
        await JsonRpcResponseWriter.WriteAsync(transport, response, EthereumJsonSerializer.JsonOptions,
            isBatch: true, CancellationToken.None);
        await transport.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Writer, Is.SameAs(transport), "materialized results must bypass recovery staging");
            Assert.That(result.InitialBytes, Is.EqualTo(1234 + "{\"jsonrpc\":\"2.0\",\"result\":"u8.Length));
        }
    }

    private sealed class CountingResult : IStreamableResult
    {
        public long InitialBytes { get; private set; }
        public PipeWriter? Writer { get; private set; }
        public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            Writer = writer;
            InitialBytes = ((CountingWriter)writer).WrittenCount;
            writer.Write("null"u8);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ChunkedResult : IStreamableResult
    {
        public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            byte[] chunk = Encoding.UTF8.GetBytes(new string('x', 1024));
            writer.Write("\""u8);
            for (int i = 0; i < 32; i++)
            {
                writer.Write(chunk);
                await StreamableResultWriter.FlushIfNeededAsync(writer, cancellationToken);
            }
            writer.Write("\""u8);
        }
    }

    private sealed class FlushCountingPipeWriter(PipeWriter inner) : PipeWriter
    {
        public int FlushCount { get; private set; }
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
        public override long UnflushedBytes => inner.UnflushedBytes;
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            return inner.FlushAsync(cancellationToken);
        }
    }

    private JsonRpcSuccessResponse CreateInvalidTransactionResponse(int commitMode, Exception? failure = null) => new()
    {
        Id = new JsonRpcId(42L),
        Result = new InvalidTransactionResult(commitMode, failure: failure),
        Streaming = CreateStreamingContext()
    };

    private sealed class InvalidTransactionResult(int commitMode, Action? beforeThrow = null, Exception? failure = null) : IStreamableResult
    {
        public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("{\"vmTrace\":"u8);
            if (commitMode == 1) await writer.FlushAsync(cancellationToken);
            if (commitMode == 2) writer.Write(new byte[20_000]);
            beforeThrow?.Invoke();
            throw failure ?? new InsufficientBalanceException(TestItem.AddressA);
        }
    }

    public enum TransportKind { Pipe, Counting, Rewindable }

    [Test]
    public async Task Observer_receives_the_result_the_client_received([Values] TransportKind transport, [Values] bool deferred, [Values] bool isBatch)
    {
        RecordingObserver observer = new();
        using JsonRpcSuccessResponse response = new() { Id = new JsonRpcId(7L), Result = new ChunkedResult(), Streaming = deferred ? CreateStreamingContext() : null };
        response.ObserveStreamedResult(observer, NullLogger.Instance);

        string envelope = await WriteAsync(transport, response, isBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observer.Result, Is.EqualTo(ResultOf(envelope)));
            Assert.That(observer.Succeeded, Is.True);
        }
    }

    [Test]
    public async Task Observed_counting_writer_stays_a_counting_writer()
    {
        using RecyclableMemoryStream stream = RecyclableStream.GetStream("test");
        CountingStreamPipeWriter transport = new(stream, initialWrittenCount: 1234);
        CountingResult result = new();
        using JsonRpcSuccessResponse response = new() { Result = result };
        response.ObserveStreamedResult(new RecordingObserver(), NullLogger.Instance);

        await JsonRpcResponseWriter.WriteAsync(transport, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        await transport.CompleteAsync();

        Assert.That(result.InitialBytes, Is.EqualTo(1234 + "{\"jsonrpc\":\"2.0\",\"result\":"u8.Length));
    }

    [Test]
    public async Task Observer_is_told_a_replaced_or_unfinished_result_did_not_succeed([Values] bool replaced)
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        RecordingObserver observer = new();
        using JsonRpcSuccessResponse response = replaced ? CreateInvalidTransactionResponse(commitMode: 0) : new() { Result = new TruncatedResult() };
        response.ObserveStreamedResult(observer, NullLogger.Instance);

        await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();

        Assert.That(observer.Succeeded, Is.False);
    }

    [Test]
    public void Observer_is_told_a_cancelled_or_failed_write_did_not_succeed([Values] bool cancelled)
    {
        RecordingObserver observer = new();
        using JsonRpcSuccessResponse response = new() { Result = new StubStreamable() };
        response.ObserveStreamedResult(observer, NullLogger.Instance);

        Assert.That(async () => await JsonRpcResponseWriter.WriteAsync(
                cancelled ? new Pipe().Writer : new FailingPipeWriter(), response, EthereumJsonSerializer.JsonOptions,
                cancelled ? new CancellationToken(canceled: true) : CancellationToken.None),
            cancelled ? Throws.InstanceOf<OperationCanceledException>() : Throws.InstanceOf<IOException>());
        Assert.That(observer.Succeeded, Is.False);
    }

    [Test]
    public async Task Observer_is_told_a_result_stopped_by_its_flush_did_not_succeed([Values] bool readerCompleted)
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        RecordingObserver observer = new();
        using JsonRpcSuccessResponse response = new() { Result = new StopsOnFlushResult(cancelFlush: !readerCompleted) };
        response.ObserveStreamedResult(observer, NullLogger.Instance);
        if (readerCompleted) await pipe.Reader.CompleteAsync();

        await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observer.Result, Is.EqualTo("[1]"));
            Assert.That(observer.Succeeded, Is.False);
        }
    }

    [Test]
    public async Task Observer_that_throws_does_not_change_the_response([Values] bool deferred)
    {
        using JsonRpcSuccessResponse unobserved = new() { Id = new JsonRpcId(7L), Result = new ChunkedResult(), Streaming = deferred ? CreateStreamingContext() : null };
        using JsonRpcSuccessResponse observed = new() { Id = new JsonRpcId(7L), Result = new ChunkedResult(), Streaming = deferred ? CreateStreamingContext() : null };
        ThrowingObserver observer = new();
        TestLogger logger = new();
        observed.ObserveStreamedResult(observer, new ILogger(logger));

        string expected = await WriteAsync(TransportKind.Pipe, unobserved, isBatch: false);
        string actual = await WriteAsync(TransportKind.Pipe, observed, isBatch: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(observer.Chunks, Is.EqualTo(1));
            Assert.That(observer.Succeeded, Is.False);
            Assert.That(logger.LogList, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task Observer_receives_a_deferred_block_trace_as_the_client_received_it()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        await chain.AddBlock(Build.A.Transaction.WithNonce(chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB))
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        JsonRpcConfig config = new() { EnabledModules = [ModuleType.Debug] };
        RpcModuleProvider moduleProvider = new(new Testably.Abstractions.RealFileSystem(), config, new EthereumJsonSerializer(), NullLogManager.Instance);
        moduleProvider.Register(new SingletonModulePool<IDebugRpcModule>(new SingletonFactory<IDebugRpcModule>(chain.DebugRpcModule), true));
        JsonRpcService service = new(moduleProvider, NullLogManager.Instance, config, _keeper);
        RecordingObserver observer = new();

        using JsonRpcResponse response = await service.SendRequestAsync(
            RpcTest.BuildJsonRequest("debug_traceBlockByNumber", "latest", new { tracer = "callTracer" }), new JsonRpcContext(RpcEndpoint.Http));
        response.ObserveStreamedResult(observer, NullLogger.Instance);
        string envelope = await WriteAsync(TransportKind.Pipe, response, isBatch: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Streaming, Is.Not.Null, "precondition: a deferred result");
            Assert.That(observer.Result, Is.EqualTo(ResultOf(envelope)));
            Assert.That(observer.Succeeded, Is.True);
        }
    }

    private static async Task<string> WriteAsync(TransportKind transport, JsonRpcResponse response, bool isBatch)
    {
        using RecyclableMemoryStream stream = RecyclableStream.GetStream("test");
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        PipeWriter writer = transport switch
        {
            TransportKind.Counting => new CountingStreamPipeWriter(stream),
            TransportKind.Rewindable => new RewindableStreamPipeWriter(stream),
            _ => pipe.Writer,
        };

        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, isBatch, CancellationToken.None);
        await writer.CompleteAsync();
        if (transport != TransportKind.Pipe)
        {
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        ReadResult read = await pipe.Reader.ReadAsync();
        string envelope = Encoding.UTF8.GetString(read.Buffer.ToArray());
        await pipe.Reader.CompleteAsync();
        return envelope;
    }

    private static string ResultOf(string envelope)
    {
        const string start = "{\"jsonrpc\":\"2.0\",\"result\":";
        Assert.That(envelope, Does.StartWith(start));
        return envelope[start.Length..envelope.LastIndexOf(",\"id\":", StringComparison.Ordinal)];
    }

    internal sealed class RecordingObserver : IStreamedResultObserver
    {
        private readonly ArrayBufferWriter<byte> _written = new();

        public string Result => Encoding.UTF8.GetString(_written.WrittenSpan);

        public bool? Succeeded { get; private set; }

        public void OnWritten(ReadOnlySpan<byte> utf8Json) => _written.Write(utf8Json);

        public void OnCompleted(bool succeeded)
        {
            Assert.That(Succeeded, Is.Null, "completed once");
            Succeeded = succeeded;
        }
    }

    private sealed class ThrowingObserver : IStreamedResultObserver
    {
        public int Chunks { get; private set; }

        public bool? Succeeded { get; private set; }

        public void OnWritten(ReadOnlySpan<byte> utf8Json)
        {
            Chunks++;
            throw new InvalidOperationException();
        }

        public void OnCompleted(bool succeeded)
        {
            Succeeded = succeeded;
            throw new InvalidOperationException();
        }
    }

    /// <summary>Stops like the status-less in-tree results do when a flush reports the reader completed or the flush cancelled.</summary>
    private sealed class StopsOnFlushResult(bool cancelFlush) : IStreamableResult
    {
        public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("[1"u8);
            if (cancelFlush) writer.CancelPendingFlush();
            FlushResult flush = await writer.FlushAsync(cancellationToken);
            if (!flush.IsCompleted && !flush.IsCanceled) writer.Write(",2"u8);
            writer.Write("]"u8);
        }
    }

    private sealed class TruncatedResult : IStreamableResultWithStatus
    {
        public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<StreamableResultStatus> WriteToWithStatusAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("[1]"u8);
            return ValueTask.FromResult(StreamableResultStatus.Truncated);
        }
    }

    [Test]
    public void Fractional_decimal_id_is_rejected_at_construction()
    {
        Action act = () => new JsonRpcId(1.5m);

        Assert.That(act, Throws.TypeOf<NotSupportedException>());
    }
}
