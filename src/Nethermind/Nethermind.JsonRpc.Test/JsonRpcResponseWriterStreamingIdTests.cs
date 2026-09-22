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
using Nethermind.Core.Test.Builders;
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

        await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), CancellationToken.None);
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
        using JsonRpcSuccessResponse response = new()
        {
            Id = new JsonRpcId(42L),
            Result = new InvalidTransactionResult(commitMode),
            StreamExceptionHandler = ex => new JsonRpcErrorResponse
            {
                Id = new JsonRpcId(42L),
                Error = new Error { Code = ErrorCodes.InvalidInput, Message = ex.Message }
            }
        };

        if (commitMode != 2)
        {
            await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), CancellationToken.None);
        }
        else
        {
            Assert.ThrowsAsync<InsufficientBalanceException>(async () =>
                await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), CancellationToken.None));
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
            Assert.That(envelope, Does.StartWith("{\"jsonrpc\":\"2.0\",\"result\":"));
            Assert.That(envelope, Does.Not.Contain("\"error\""));
        }
    }

    [Test]
    public async Task Stream_failure_propagates_without_handler_or_after_transport_cancellation([Values] bool cancelTransport)
    {
        using CancellationTokenSource cancellation = new();
        using JsonRpcSuccessResponse response = new()
        {
            Result = new InvalidTransactionResult(0, cancelTransport ? cancellation.Cancel : null),
            StreamExceptionHandler = cancelTransport
                ? _ => throw new AssertionException("A cancelled transport must not receive a replacement response")
                : null
        };
        Pipe pipe = new();
        try
        {
            Assert.ThrowsAsync<InsufficientBalanceException>(async () =>
                await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), cancellation.Token));
            await pipe.Writer.CompleteAsync();
            ReadResult read = await pipe.Reader.ReadAsync();
            Assert.That(read.Buffer.IsEmpty, Is.True);
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }
    }

    [Test]
    public void Transport_write_failure_is_not_mapped_to_an_rpc_error()
    {
        using JsonRpcSuccessResponse response = new()
        {
            Result = new InvalidTransactionResult(2),
            StreamExceptionHandler = _ => throw new AssertionException("Transport failures must propagate")
        };
        Assert.ThrowsAsync<IOException>(async () =>
            await JsonRpcResponseWriter.WriteAsync(new FailingPipeWriter(), response, new JsonSerializerOptions(), CancellationToken.None));
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
            new JsonSerializerOptions(), CancellationToken.None);
        await transport.CompleteAsync();
        await pipe.Reader.CompleteAsync();

        Assert.That(transport.FlushCount, Is.EqualTo(2));
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
    public void Fractional_decimal_id_is_rejected_at_construction()
    {
        Action act = () => new JsonRpcId(1.5m);

        Assert.That(act, Throws.TypeOf<NotSupportedException>());
    }
}
