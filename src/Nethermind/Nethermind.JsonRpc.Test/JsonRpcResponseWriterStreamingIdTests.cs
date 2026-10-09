// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;
using Nethermind.Core.Resettables;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Core.Memory;
using Nethermind.Core.Collections;
using Nethermind.Facade.Filters;
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
    public async Task Validation_failure_replaces_only_uncommitted_response([Values(0, 1, 2)] int commitMode)
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        using JsonRpcSuccessResponse response = CreateInvalidTransactionResponse(commitMode);

        if (commitMode != 2)
        {
            await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<InsufficientBalanceException>(async () =>
                await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None));
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
                Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidInput));
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
    public async Task Stream_failure_propagates_without_handler_or_after_transport_cancellation([Values] bool cancelTransport)
    {
        using CancellationTokenSource cancellation = new();
        using JsonRpcSuccessResponse response = new()
        {
            Result = new InvalidTransactionResult(0, cancelTransport ? cancellation.Cancel : null),
            Streaming = cancelTransport ? CreateStreamingContext() : null
        };
        Pipe pipe = new();
        try
        {
            await Assert.ThrowsAsync<InsufficientBalanceException>(async () =>
                await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, EthereumJsonSerializer.JsonOptions, cancellation.Token));
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

    private JsonRpcSuccessResponse CreateInvalidTransactionResponse(int commitMode) => new()
    {
        Id = new JsonRpcId(42L),
        Result = new InvalidTransactionResult(commitMode),
        Streaming = CreateStreamingContext()
    };

    private sealed class InvalidTransactionResult(int commitMode, Action? beforeThrow = null) : IStreamableResult
    {
        public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("{\"vmTrace\":"u8);
            if (commitMode == 1) await writer.FlushAsync(cancellationToken);
            if (commitMode == 2) writer.Write(new byte[20_000]);
            beforeThrow?.Invoke();
            throw new InsufficientBalanceException(TestItem.AddressA);
        }
    }

    [Test]
    public async Task Materialized_logs_preserve_exact_response_bytes(
        [Values(0, 1, 127, 128, 512)] int count, [Values] bool typed, [Values] bool indented)
    {
        using JsonRpcResponse response = CreateLogsResponse(count, typed, new JsonRpcId("a\"\\\n"));
        JsonSerializerOptions options = indented ? EthereumJsonSerializer.JsonOptionsIndented : EthereumJsonSerializer.JsonOptions;
        ArrayBufferWriter<byte> expected = new();
        JsonRpcResponseWriter.Write(expected, response, options);
        RecordingPipeWriter writer = new();

        await JsonRpcResponseWriter.WriteAsync(writer, response, options, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.Bytes, Is.EqualTo(expected.WrittenSpan.ToArray()));
            Assert.That(writer.FlushCount > 1, Is.EqualTo(count >= 128 && !indented));
            if (count >= 128 && !indented) Assert.That(writer.PeakUnflushedBytes, Is.LessThan(expected.WrittenCount / 2));
        }
        using JsonDocument document = JsonDocument.Parse(writer.Bytes);
        Assert.That(document.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(count));
    }

    private static IEnumerable<TestCaseData> MaterializedLogIdCases()
    {
        foreach (TestCaseData testCase in IdCases())
        {
            yield return new TestCaseData(testCase.Arguments).SetName("MaterializedLogs_" + testCase.TestName);
        }
    }

    [TestCaseSource(nameof(MaterializedLogIdCases))]
    public async Task Materialized_logs_preserve_id(JsonRpcId id, string serializedId)
    {
        using JsonRpcResponse response = CreateLogsResponse(128, typed: true, id);
        RecordingPipeWriter writer = new();
        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(writer.Bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.GetProperty("id").GetRawText(), Is.EqualTo(serializedId));
            Assert.That(document.RootElement.GetProperty("jsonrpc").GetString(), Is.EqualTo("2.0"));
            Assert.That(document.RootElement.GetProperty("result")[0].GetProperty("logIndex").GetString(), Is.EqualTo("0x0"));
            Assert.That(document.RootElement.TryGetProperty("_streamStatus", out _), Is.False);
        }
    }

    [Test]
    public async Task Materialized_logs_wait_for_backpressure_before_disposal()
    {
        TrackingLogPool pool = new();
        JsonRpcResponse response = CreateLogsResponse(512, typed: true, new JsonRpcId(42), pool);
        RecordingPipeWriter writer = new() { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Task write = JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None).AsTask();
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(write.IsCompleted, Is.False);
                Assert.That(writer.FlushCount, Is.EqualTo(1));
                Assert.That(pool.Returns, Is.Zero);
            }
            writer.Gate.SetResult(new FlushResult(false, false));
            await write;
            Assert.That(pool.Returns, Is.Zero);
        }
        finally
        {
            writer.Gate.TrySetResult(new FlushResult(false, false));
            try { await write; }
            finally { response.Dispose(); }
        }
        Assert.That(pool.Returns, Is.EqualTo(1));
    }

    [Test]
    public async Task Materialized_logs_propagate_transport_failure([Values(1, 2, 3)] int failure)
    {
        TrackingLogPool pool = new();
        JsonRpcResponse response = CreateLogsResponse(512, typed: true, new JsonRpcId(42), pool);
        RecordingPipeWriter writer = new() { Failure = failure };
        try
        {
            if (failure == 3)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(async () =>
                    await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pool.Returns, Is.Zero);
                Assert.That(writer.FlushCount, Is.EqualTo(1));
                Assert.That(Encoding.UTF8.GetString(writer.Bytes), Does.Not.EndWith("\"id\":42}"));
            }
        }
        finally
        {
            response.Dispose();
        }
        Assert.That(pool.Returns, Is.EqualTo(1));
    }

    [Test]
    public async Task Materialized_logs_pre_cancelled_write_emits_nothing()
    {
        using JsonRpcResponse response = CreateLogsResponse(128, typed: true, new JsonRpcId(42));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        RecordingPipeWriter writer = new();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, cancellation.Token));

        Assert.That(writer.Bytes, Is.Empty);
    }

    [Test]
    public async Task Materialized_logs_propagate_suspended_flush_failure([Values(1, 2, 3, 4)] int failure)
    {
        TrackingLogPool pool = new();
        JsonRpcResponse response = CreateLogsResponse(512, typed: true, new JsonRpcId(42), pool);
        using CancellationTokenSource cancellation = new();
        RecordingPipeWriter writer = new() { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        ValueTask write = JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, cancellation.Token);
        bool suspended = !write.IsCompleted;
        CancellationToken observedToken = writer.LastToken;
        try
        {
            if (failure == 1) writer.Gate.SetException(new IOException("Transport failed"));
            else
            {
                if (failure == 4) cancellation.Cancel();
                writer.Gate.SetResult(new FlushResult(failure == 3, failure == 2));
            }
            if (failure >= 3) await Assert.ThrowsAsync<OperationCanceledException>(async () => await write);
            else await Assert.ThrowsAsync<IOException>(async () => await write);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(suspended, Is.True);
                Assert.That(observedToken, Is.EqualTo(cancellation.Token));
                Assert.That(pool.Returns, Is.Zero);
                Assert.That(writer.FlushCount, Is.GreaterThanOrEqualTo(1));
                if (failure != 4) Assert.That(writer.FlushCount, Is.EqualTo(1));
                Assert.That(Encoding.UTF8.GetString(writer.Bytes), Does.Not.EndWith("\"id\":42}"));
            }
        }
        finally
        {
            writer.Gate.TrySetResult(new FlushResult(false, false));
            response.Dispose();
        }
        Assert.That(pool.Returns, Is.EqualTo(1));
    }

    [Test]
    public async Task Materialized_logs_unsupported_shapes_keep_sync_contract([Values(0, 1, 2, 3)] int shape)
    {
        using JsonRpcResponse response = CreateLogsResponse(128, typed: true, new JsonRpcId(42), shape: shape);
        ArrayBufferWriter<byte> expected = new();
        JsonRpcResponseWriter.Write(expected, response, EthereumJsonSerializer.JsonOptions);
        RecordingPipeWriter writer = new() { SupportsUnflushedBytes = shape != 0 };

        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.Bytes, Is.EqualTo(expected.WrittenSpan.ToArray()));
            Assert.That(writer.FlushCount, Is.Zero);
        }
    }

    [Test]
    public async Task Materialized_logs_preserve_batch_and_buffering([Values] bool buffered)
    {
        using JsonRpcResponse response = CreateLogsResponse(512, typed: true, new JsonRpcId(42));
        ArrayBufferWriter<byte> expected = new();
        JsonRpcResponseWriter.Write(expected, response, EthereumJsonSerializer.JsonOptions);
        using RecyclableMemoryStream stream = RecyclableStream.GetStream("test");
        CountingWriter writer = buffered ? new RewindableStreamPipeWriter(stream, 1234) : new CountingStreamPipeWriter(stream, initialWrittenCount: 1234);
        writer.Write("[null,"u8);

        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, isBatch: true, CancellationToken.None);
        JsonRpcResponseWriter.WriteBatchEnd(writer);
        await writer.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.WrittenCount, Is.EqualTo(1234 + 7 + expected.WrittenCount));
            Assert.That(stream.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes("[null," + Encoding.UTF8.GetString(expected.WrittenSpan) + "]")));
        }
    }

    [Test]
    public async Task Materialized_logs_custom_options_preserve_sync_contract([Values(0, 1, 2, 3)] int custom)
    {
        using JsonRpcResponse response = CreateLogsResponse(128, typed: true, new JsonRpcId(42));
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions);
        if (custom == 0) options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        if (custom == 1) options.ReferenceHandler = ReferenceHandler.Preserve;
        if (custom == 2) options.Converters.Insert(0, new TestLogConverter());
        if (custom == 3) options.Converters.Insert(0, new TestLogListConverter());
        ArrayBufferWriter<byte> expected = new();
        JsonRpcResponseWriter.Write(expected, response, options);
        RecordingPipeWriter writer = new();

        await JsonRpcResponseWriter.WriteAsync(writer, response, options, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.Bytes, Is.EqualTo(expected.WrittenSpan.ToArray()));
            Assert.That(writer.FlushCount, Is.Zero);
        }
    }

    [Test]
    public async Task Materialized_logs_custom_max_depth_keeps_error()
    {
        using JsonRpcResponse response = CreateLogsResponse(128, typed: true, new JsonRpcId(42));
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions) { MaxDepth = 2 };
        RecordingPipeWriter writer = new();
        await Assert.ThrowsAsync<JsonException>(async () =>
            await JsonRpcResponseWriter.WriteAsync(writer, response, options, CancellationToken.None));
        Assert.That(writer.FlushCount, Is.Zero);
    }

    private static JsonRpcResponse CreateLogsResponse(int count, bool typed, JsonRpcId id, TrackingLogPool? pool = null, int shape = 0)
    {
        ArrayPoolList<FilterLog> logs = new(pool ?? (ArrayPool<FilterLog>)ArrayPool<FilterLog>.Shared, count);
        for (int i = 0; i < count; i++)
        {
            byte[] data = new byte[64];
            data[0] = (byte)i;
            data[^1] = 255;
            logs.Add(new FilterLog(i, 123, 456, TestItem.KeccakA, i / 2, TestItem.KeccakB, TestItem.AddressA,
                data, [TestItem.KeccakC], removed: i % 2 == 0));
        }
        JsonRpcResponse response = shape switch
        {
            1 => new DerivedSuccessLogsResponse { Result = logs },
            2 => new DerivedTypedLogsResponse(logs),
            3 => ResultWrapper<IEnumerable<FilterLog>>.Fail("unavailable", ErrorCodes.ResourceUnavailable, logs),
            _ => typed ? ResultWrapper<IEnumerable<FilterLog>>.Success(logs) : new JsonRpcSuccessResponse { Result = logs }
        };
        response.Id = id;
        return response;
    }

    [Test]
    public void Fractional_decimal_id_is_rejected_at_construction()
    {
        Action act = () => new JsonRpcId(1.5m);

        Assert.That(act, Throws.TypeOf<NotSupportedException>());
    }

    private sealed class DerivedSuccessLogsResponse : JsonRpcSuccessResponse
    {
        internal override void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options) => writer.WriteStringValue("derived-success");
    }

    private sealed class DerivedTypedLogsResponse : ResultWrapper<IEnumerable<FilterLog>>
    {
        public DerivedTypedLogsResponse(IEnumerable<FilterLog> logs) => Data = logs;
        internal override void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options) => writer.WriteStringValue("derived-typed");
    }

    private sealed class TrackingLogPool : ArrayPool<FilterLog>
    {
        public int Returns { get; private set; }
        public override FilterLog[] Rent(int minimumLength) => new FilterLog[minimumLength];
        public override void Return(FilterLog[] array, bool clearArray = false) => Returns++;
    }

    private sealed class RecordingPipeWriter : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private int _flushed;
        public int FlushCount { get; private set; }
        public long PeakUnflushedBytes { get; private set; }
        public int Failure { get; init; }
        public bool SupportsUnflushedBytes { get; init; } = true;
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource<FlushResult>? Gate { get; init; }
        public byte[] Bytes => _buffer.WrittenSpan.ToArray();
        public override bool CanGetUnflushedBytes => SupportsUnflushedBytes;
        public override long UnflushedBytes => SupportsUnflushedBytes ? _buffer.WrittenCount - _flushed : throw new NotSupportedException();
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override void Advance(int bytes)
        {
            _buffer.Advance(bytes);
            PeakUnflushedBytes = Math.Max(PeakUnflushedBytes, _buffer.WrittenCount - _flushed);
        }
        public override void Complete(Exception? exception = null) { }
        public override void CancelPendingFlush() => throw new NotSupportedException();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastToken = cancellationToken;
            FlushCount++;
            _flushed = _buffer.WrittenCount;
            if (Failure == 1) throw new IOException("Transport failed");
            if (Gate is not null && FlushCount == 1) return new(Gate.Task);
            return new(new FlushResult(Failure == 3, Failure == 2));
        }
    }

    private sealed class TestLogConverter : JsonConverter<FilterLog>
    {
        public override FilterLog Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, FilterLog value, JsonSerializerOptions options) => writer.WriteStringValue("custom-log");
    }

    private sealed class TestLogListConverter : JsonConverter<ArrayPoolList<FilterLog>>
    {
        public override ArrayPoolList<FilterLog> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ArrayPoolList<FilterLog> value, JsonSerializerOptions options) => writer.WriteStringValue("custom-list");
    }

}
