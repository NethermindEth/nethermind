// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
using FluentAssertions;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;
using NUnit.Framework;

#nullable enable

namespace Nethermind.Runner.Test.JsonRpc;

/// <summary>
/// Regression coverage for JSON-RPC response writer behavior.
///
/// Before this change (pure master):
/// Startup used CountingPipeWriter(ctx.Response.BodyWriter), which can accumulate
/// the full response in PipeWriter internal buffers during serialization.
///
/// After this change:
/// Startup uses CountingStreamPipeWriter(ctx.Response.Body), which flushes
/// incrementally and keeps peak unflushed memory bounded.
/// </summary>
[TestFixture]
public class CountingWriterTests
{
    private static readonly EthereumJsonSerializer Serializer = new();

    [TestCase(1)]
    [TestCase(5)]
    public async Task PeakBuffer_MasterPathBuffersFullPayload_ThisChangeRemainsBounded(int sizeMb)
    {
        int totalBytes = sizeMb * 1024 * 1024;
        JsonRpcSuccessResponse response = new() { Id = 1, Result = new string('Z', totalBytes) };

        (long masterPeak, long payloadSize) = await MeasurePeakAsyncUsingPipeWriter(response);
        (long changedPeak, long changedPayloadSize) = await MeasurePeakAsyncUsingStreamWriter(response);

        payloadSize.Should().BeGreaterThan(totalBytes);
        changedPayloadSize.Should().Be(payloadSize);
        masterPeak.Should().BeGreaterThan(totalBytes);
        changedPeak.Should().BeLessThan(64 * 1024);
        masterPeak.Should().BeGreaterThan(changedPeak * 10);
    }

    [Test]
    public async Task PeakBuffer_Batch_MasterPathBuffersMost_ThisChangeRemainsBounded()
    {
        const int entryCount = 100;
        const int entryDataSize = 5_000;

        (long masterPeak, long payloadSize) = await MeasureBatchPeakAsyncUsingPipeWriter(entryCount, entryDataSize);
        (long changedPeak, long changedPayloadSize) = await MeasureBatchPeakAsyncUsingStreamWriter(entryCount, entryDataSize);

        changedPayloadSize.Should().Be(payloadSize);
        masterPeak.Should().BeGreaterThan(payloadSize / 2);
        changedPeak.Should().BeLessThan(64 * 1024);
        masterPeak.Should().BeGreaterThan(changedPeak * 5);
    }

    [Test]
    public async Task ThisChange_ConsumerReceivesDataDuringSerialization()
    {
        int totalBytes = 2 * 1024 * 1024;
        JsonRpcSuccessResponse response = new() { Id = 1, Result = new string('W', totalBytes) };

        MidpointTrackingStream stream = new();
        CountingStreamPipeWriter writer = new(stream);

        await Serializer.SerializeAsync(writer, response);
        long bytesReceivedDuringSerialization = stream.Position;
        await writer.CompleteAsync();
        long bytesReceivedAfterComplete = stream.Position;

        bytesReceivedDuringSerialization.Should().BeGreaterThan(totalBytes / 2);
        bytesReceivedAfterComplete.Should().BeGreaterThan(totalBytes);
    }

    [TestCase(16)]
    [TestCase(1_048_576)]
    public async Task SerializationParity_SuccessResponse(int payloadSize)
    {
        JsonRpcSuccessResponse response = new() { Id = 1, Result = new string('A', payloadSize) };
        await AssertResponseParityAsync(response);
    }

    [Test]
    public async Task SerializationParity_ErrorResponse()
    {
        JsonRpcErrorResponse response = new()
        {
            Id = 42,
            Error = new Error { Code = -32600, Message = "Invalid request" }
        };

        await AssertResponseParityAsync(response);
    }

    [Test]
    public async Task SerializationParity_LargeStructuredArray()
    {
        List<Dictionary<string, string>> largeResult = new(1_000);
        for (int i = 0; i < 1_000; i++)
        {
            largeResult.Add(new Dictionary<string, string>
            {
                ["address"] = "0x" + i.ToString("x40"),
                ["data"] = "0x" + new string('a', 256),
                ["blockNumber"] = "0x" + i.ToString("x"),
                ["transactionHash"] = "0x" + new string((char)('0' + (i % 10)), 64),
            });
        }

        JsonRpcSuccessResponse response = new() { Id = 1, Result = largeResult };
        await AssertResponseParityAsync(response);
    }

    [TestCase(1)]
    [TestCase(50)]
    public async Task SerializationParity_BatchResponse(int batchSize)
    {
        byte[] openBracket = "["u8.ToArray();
        byte[] comma = ","u8.ToArray();
        byte[] closeBracket = "]"u8.ToArray();

        Pipe pipe = new();
        CountingPipeWriter pipeWriter = new(pipe.Writer);
        pipeWriter.Write(openBracket);
        for (int i = 0; i < batchSize; i++)
        {
            if (i > 0)
            {
                pipeWriter.Write(comma);
            }

            JsonRpcSuccessResponse entry = new() { Id = i, Result = $"result_{i}" };
            await Serializer.SerializeAsync(pipeWriter, entry);
        }

        pipeWriter.Write(closeBracket);
        await pipeWriter.CompleteAsync();
        ReadResult pipeRead = await pipe.Reader.ReadAsync();
        byte[] pipeBytes = pipeRead.Buffer.ToArray();
        pipe.Reader.AdvanceTo(pipeRead.Buffer.End);
        await pipe.Reader.CompleteAsync();

        MemoryStream stream = new();
        CountingStreamPipeWriter streamWriter = new(stream);
        streamWriter.Write(openBracket);
        for (int i = 0; i < batchSize; i++)
        {
            if (i > 0)
            {
                streamWriter.Write(comma);
            }

            JsonRpcSuccessResponse entry = new() { Id = i, Result = $"result_{i}" };
            await Serializer.SerializeAsync(streamWriter, entry);
        }

        streamWriter.Write(closeBracket);
        await streamWriter.CompleteAsync();

        byte[] streamBytes = stream.ToArray();
        streamBytes.Should().Equal(pipeBytes);
        streamWriter.WrittenCount.Should().Be(pipeWriter.WrittenCount);

        string json = Encoding.UTF8.GetString(streamBytes);
        JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(batchSize);
    }

    private static async Task AssertResponseParityAsync<T>(T response)
    {
        (byte[] masterBytes, long masterCount) = await SerializeViaPipeWriterAsync(response);
        (byte[] changedBytes, long changedCount) = await SerializeViaStreamWriterAsync(response);

        changedBytes.Should().Equal(masterBytes);
        changedCount.Should().Be(masterCount);
    }

    private static async Task<(long Peak, long PayloadSize)> MeasurePeakAsyncUsingPipeWriter<T>(T response)
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: long.MaxValue));
        PeakTrackingWriter writer = new(new CountingPipeWriter(pipe.Writer));

        await Serializer.SerializeAsync(writer, response);
        long peak = writer.PeakUnflushedBytes;
        await writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        long payloadSize = readResult.Buffer.Length;
        pipe.Reader.AdvanceTo(readResult.Buffer.End);
        await pipe.Reader.CompleteAsync();

        return (peak, payloadSize);
    }

    private static async Task<(long Peak, long PayloadSize)> MeasurePeakAsyncUsingStreamWriter<T>(T response)
    {
        MemoryStream stream = new();
        PeakTrackingWriter writer = new(new CountingStreamPipeWriter(stream));

        await Serializer.SerializeAsync(writer, response);
        long peak = writer.PeakUnflushedBytes;
        await writer.CompleteAsync();

        return (peak, stream.Length);
    }

    private static async Task<(long Peak, long PayloadSize)> MeasureBatchPeakAsyncUsingPipeWriter(int entryCount, int entryDataSize)
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: long.MaxValue));
        PeakTrackingWriter writer = new(new CountingPipeWriter(pipe.Writer));

        await WriteBatchResponseAsync(writer, entryCount, entryDataSize);
        long peak = writer.PeakUnflushedBytes;
        await writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        long payloadSize = readResult.Buffer.Length;
        pipe.Reader.AdvanceTo(readResult.Buffer.End);
        await pipe.Reader.CompleteAsync();

        return (peak, payloadSize);
    }

    private static async Task<(long Peak, long PayloadSize)> MeasureBatchPeakAsyncUsingStreamWriter(int entryCount, int entryDataSize)
    {
        MemoryStream stream = new();
        PeakTrackingWriter writer = new(new CountingStreamPipeWriter(stream));

        await WriteBatchResponseAsync(writer, entryCount, entryDataSize);
        long peak = writer.PeakUnflushedBytes;
        await writer.CompleteAsync();

        return (peak, stream.Length);
    }

    private static async Task<(byte[] Bytes, long WrittenCount)> SerializeViaPipeWriterAsync<T>(T value)
    {
        Pipe pipe = new();
        CountingPipeWriter writer = new(pipe.Writer);

        await Serializer.SerializeAsync(writer, value);
        await writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        byte[] bytes = readResult.Buffer.ToArray();
        pipe.Reader.AdvanceTo(readResult.Buffer.End);
        await pipe.Reader.CompleteAsync();

        return (bytes, writer.WrittenCount);
    }

    private static async Task<(byte[] Bytes, long WrittenCount)> SerializeViaStreamWriterAsync<T>(T value)
    {
        MemoryStream stream = new();
        CountingStreamPipeWriter writer = new(stream);

        await Serializer.SerializeAsync(writer, value);
        await writer.CompleteAsync();

        return (stream.ToArray(), writer.WrittenCount);
    }

    private static async Task WriteBatchResponseAsync(PipeWriter writer, int entryCount, int entryDataSize)
    {
        writer.Write("["u8);
        for (int i = 0; i < entryCount; i++)
        {
            if (i > 0)
            {
                writer.Write(","u8);
            }

            string data = new('x', entryDataSize);
            JsonRpcSuccessResponse entry = new() { Id = i, Result = data };
            await Serializer.SerializeAsync(writer, entry);
        }

        writer.Write("]"u8);
    }

    /// <summary>
    /// Wraps a PipeWriter and tracks peak unflushed bytes observed after each Advance call.
    /// </summary>
    private sealed class PeakTrackingWriter(PipeWriter inner) : PipeWriter
    {
        public long PeakUnflushedBytes { get; private set; }

        public override void Advance(int bytes)
        {
            inner.Advance(bytes);
            long unflushed = inner.UnflushedBytes;
            if (unflushed > PeakUnflushedBytes)
            {
                PeakUnflushedBytes = unflushed;
            }
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
        public override long UnflushedBytes => inner.UnflushedBytes;
    }

    private sealed class MidpointTrackingStream : MemoryStream { }
}

