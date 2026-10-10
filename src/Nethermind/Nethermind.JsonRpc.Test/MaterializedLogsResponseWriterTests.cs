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
using Nethermind.Core.Collections;
using Nethermind.Core.Resettables;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
public class MaterializedLogsResponseWriterTests
{
    [Test]
    public async Task Materialized_logs_preserve_exact_response_bytes(
        [Values(0, 1, 127, 128, 512, 4096)] int count, [Values] bool typed, [Values] bool indented)
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
            if (count >= 128 && !indented) Assert.That(writer.PeakUnflushedBytes, Is.LessThanOrEqualTo(1024 * 1024 + 1024));
        }
        using JsonDocument document = JsonDocument.Parse(writer.Bytes);
        Assert.That(document.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(count));
    }

    [Test]
    public async Task Materialized_logs_WhenOutputExceedsOneMiB_CoalesceSubsequentFlushes()
    {
        using JsonRpcResponse response = CreateLogsResponse(4096, typed: true, new JsonRpcId(42));
        RecordingPipeWriter writer = new();

        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.FirstFlushBytes, Is.LessThanOrEqualTo(16 * 1024 + 1024), "first-byte delivery stays prompt");
            Assert.That(writer.SecondFlushBytes, Is.GreaterThanOrEqualTo(1024 * 1024), "subsequent output is coalesced before flushing");
            Assert.That(writer.PeakUnflushedBytes, Is.LessThanOrEqualTo(1024 * 1024 + 1024), "backpressure stays bounded by a chunk plus one log");
            Assert.That(writer.FlushCount, Is.LessThanOrEqualTo((writer.Bytes.Length + 1024 * 1024 - 1) / (1024 * 1024) + 1),
                "transport flushes scale with bytes per chunk rather than the smaller first flush");
        }
    }

    [TestCase(false, TestName = "MaterializedLogs_NullEntriesPreserveBytes")]
    [TestCase(true, TestName = "MaterializedLogs_OversizedEntryPreservesBytesAndBackpressure")]
    public async Task Materialized_logs_unusual_entries_preserve_exact_bytes(bool oversized)
    {
        using JsonRpcResponse response = CreateLogsResponse(128, typed: true, new JsonRpcId(42), oversizedDataBytes: oversized ? 128 * 1024 : 0);
        ArrayPoolList<FilterLog> logs = (ArrayPoolList<FilterLog>)((ResultWrapper<IEnumerable<FilterLog>>)response).Data!;
        logs[0] = null!;
        ArrayBufferWriter<byte> expected = new();
        JsonRpcResponseWriter.Write(expected, response, EthereumJsonSerializer.JsonOptions);
        RecordingPipeWriter writer = new();

        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.Bytes, Is.EqualTo(expected.WrittenSpan.ToArray()));
            Assert.That(writer.FlushCount, Is.GreaterThan(1));
            Assert.That(writer.PeakUnflushedBytes, Is.LessThanOrEqualTo(1024 * 1024 + (oversized ? 256 * 1024 : 0) + 1024));
        }
    }

    private static IEnumerable<TestCaseData> MaterializedLogIdCases()
    {
        foreach (TestCaseData testCase in JsonRpcResponseWriterStreamingIdTests.IdCases())
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
                Assert.That(writer.FirstFlushBytes, Is.LessThanOrEqualTo(16 * 1024 + 1024));
                Assert.That(Encoding.UTF8.GetString(writer.Bytes), Does.Not.Contain("\"id\":42"));
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

    [TestCase(1, TestName = "MaterializedLogs_SynchronousTransportFailure")]
    [TestCase(2, TestName = "MaterializedLogs_CompletedReaderIsCancellation")]
    [TestCase(3, TestName = "MaterializedLogs_CanceledFlush")]
    public async Task Materialized_logs_propagate_transport_failure(int failure)
    {
        TrackingLogPool pool = new();
        JsonRpcResponse response = CreateLogsResponse(512, typed: true, new JsonRpcId(42), pool);
        RecordingPipeWriter writer = new() { Failure = failure };
        try
        {
            if (failure >= 2)
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
    public async Task Materialized_logs_buffer_commit_failure_does_not_retry_on_dispose()
    {
        TrackingLogPool pool = new();
        JsonRpcResponse response = CreateLogsResponse(128, typed: true, new JsonRpcId(42), pool);
        RecordingPipeWriter writer = new() { FailAdvance = true };
        try
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(writer.AdvanceCount, Is.EqualTo(1));
                Assert.That(writer.FlushCount, Is.Zero);
                Assert.That(pool.Returns, Is.Zero);
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

    [TestCase(1, TestName = "MaterializedLogs_AsynchronousTransportFailure")]
    [TestCase(2, TestName = "MaterializedLogs_AsynchronousCompletedReaderIsCancellation")]
    [TestCase(3, TestName = "MaterializedLogs_AsynchronousCanceledFlush")]
    [TestCase(4, TestName = "MaterializedLogs_TokenCanceledDuringFlush")]
    public async Task Materialized_logs_propagate_suspended_flush_failure(int failure)
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
            if (failure >= 2) await Assert.ThrowsAsync<OperationCanceledException>(async () => await write);
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

    [TestCase(0, TestName = "MaterializedLogs_WriterWithoutUnflushedBytes")]
    [TestCase(1, TestName = "MaterializedLogs_DerivedSuccessResponse")]
    [TestCase(2, TestName = "MaterializedLogs_DerivedTypedResponse")]
    [TestCase(3, TestName = "MaterializedLogs_ErrorResponse")]
    public async Task Materialized_logs_unsupported_shapes_keep_sync_contract(int shape)
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

    [TestCase(0, TestName = "MaterializedLogs_CustomNamingPolicy")]
    [TestCase(1, TestName = "MaterializedLogs_CustomReferenceHandler")]
    [TestCase(2, TestName = "MaterializedLogs_CustomLogConverter")]
    [TestCase(3, TestName = "MaterializedLogs_CustomListConverter")]
    public async Task Materialized_logs_custom_options_preserve_sync_contract(int custom)
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

    private static JsonRpcResponse CreateLogsResponse(int count, bool typed, JsonRpcId id, TrackingLogPool? pool = null, int shape = 0, int oversizedDataBytes = 0)
    {
        ArrayPoolList<FilterLog> logs = new(pool ?? (ArrayPool<FilterLog>)ArrayPool<FilterLog>.Shared, count);
        for (int i = 0; i < count; i++)
        {
            byte[] data = new byte[i == count / 2 && oversizedDataBytes > 0 ? oversizedDataBytes : 64];
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
        public int FirstFlushBytes { get; private set; }
        public int SecondFlushBytes { get; private set; }
        public long PeakUnflushedBytes { get; private set; }
        public int Failure { get; init; }
        public bool FailAdvance { get; init; }
        public int AdvanceCount { get; private set; }
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
            AdvanceCount++;
            if (FailAdvance) throw new IOException("Buffer commit failed");
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
            if (FlushCount == 1) FirstFlushBytes = _buffer.WrittenCount;
            if (FlushCount == 2) SecondFlushBytes = _buffer.WrittenCount - _flushed;
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
