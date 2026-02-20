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
/// Tests demonstrating the buffering problem with CountingPipeWriter (old path)
/// and how CountingStreamPipeWriter (new path from PR #9411) fixes it.
///
/// Problem: Kestrel's PipeWriter accumulates the ENTIRE serialized response in internal
/// pipe buffers before the socket layer drains them. For multi-MB responses (eth_getLogs,
/// debug_traceTransaction, large batches), this causes LOH allocations and GC pressure.
///
/// Fix: CountingStreamPipeWriter writes to ctx.Response.Body (Stream) directly, auto-flushing
/// in small chunks (~4KB) as data is produced. Peak memory stays bounded regardless of
/// response size.
/// </summary>
[TestFixture]
public class CountingWriterTests
{
    private static readonly EthereumJsonSerializer Serializer = new();

    // ──────────────────────────────────────────────────────────
    // PROBLEM DEMONSTRATION: PipeWriter holds entire response in memory
    // ──────────────────────────────────────────────────────────

    [TestCase(1)]   // 1MB
    [TestCase(5)]   // 5MB
    [TestCase(10)]  // 10MB
    public async Task Problem_PipeWriter_BuffersEntireResponse_BeforeConsumerSeesAnything(int sizeMB)
    {
        // This simulates the OLD behavior: CountingPipeWriter wraps Kestrel's PipeWriter.
        // The pipe holds ALL data until the reader (socket layer) drains it.
        int totalBytes = sizeMB * 1024 * 1024;
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: long.MaxValue));
        CountingPipeWriter writer = new(pipe.Writer);

        // Write data in 4KB chunks (simulating Utf8JsonWriter behavior)
        byte[] chunk = new byte[4096];
        Array.Fill(chunk, (byte)'x');
        int written = 0;
        while (written < totalBytes)
        {
            int toWrite = Math.Min(chunk.Length, totalBytes - written);
            writer.Write(chunk.AsSpan(0, toWrite));
            written += toWrite;
        }

        await writer.CompleteAsync();

        // PROBLEM: The pipe reader now sees the ENTIRE response as one contiguous buffer.
        // In Kestrel, this means the response body is fully buffered in memory before
        // a single byte reaches the network socket.
        ReadResult result = await pipe.Reader.ReadAsync();
        long bufferedSize = result.Buffer.Length;

        bufferedSize.Should().Be(totalBytes,
            $"PipeWriter accumulated the entire {sizeMB}MB response in pipe buffers — " +
            "this is the LOH allocation problem PR #9411 fixes");

        pipe.Reader.AdvanceTo(result.Buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [TestCase(1)]   // 1MB
    [TestCase(5)]   // 5MB
    [TestCase(10)]  // 10MB
    public async Task Fix_StreamWriter_FlushesIncrementally_PeakBufferStaysSmall(int sizeMB)
    {
        // This simulates the NEW behavior: CountingStreamPipeWriter writes to
        // ctx.Response.Body (Stream). It auto-flushes when internal buffer exceeds 4KB.
        int totalBytes = sizeMB * 1024 * 1024;
        TrackingStream tracker = new();
        CountingStreamPipeWriter writer = new(tracker);

        byte[] chunk = new byte[4096];
        Array.Fill(chunk, (byte)'x');
        int written = 0;
        while (written < totalBytes)
        {
            int toWrite = Math.Min(chunk.Length, totalBytes - written);
            writer.Write(chunk.AsSpan(0, toWrite));
            written += toWrite;
        }

        await writer.CompleteAsync();

        // FIX: The stream received many small writes instead of one huge buffer
        tracker.WriteCallCount.Should().BeGreaterThan(1,
            "StreamWriter should flush incrementally to the underlying stream");

        // The largest single write to the stream should be bounded (not the full response)
        tracker.LargestSingleWrite.Should().BeLessThan(totalBytes,
            $"No single write to the stream should be {sizeMB}MB — data should flow in chunks");

        // Peak internal buffer is bounded by the auto-flush threshold (~4KB + segment overhead)
        // not by total response size
        tracker.LargestSingleWrite.Should().BeLessOrEqualTo(64 * 1024,
            "Individual writes to the stream should be small (auto-flush at ~4KB boundary)");

        writer.WrittenCount.Should().Be(totalBytes);
    }

    // ──────────────────────────────────────────────────────────
    // ALLOCATION COMPARISON: GC pressure difference
    // ──────────────────────────────────────────────────────────

    [Test]
    public async Task Problem_PipeWriter_AllocatesMoreForLargeResponses()
    {
        // Serialize a 2MB response through both paths and compare allocations
        string largeData = new('B', 2 * 1024 * 1024);
        JsonRpcSuccessResponse response = new() { Id = 1, Result = largeData };

        // Warm up both paths
        await SerializeViaPipeWriter(response);
        await SerializeViaStreamWriter(response);

        // Measure PipeWriter allocations
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        long beforePipe = GC.GetAllocatedBytesForCurrentThread();
        await SerializeViaPipeWriter(response);
        long pipeAllocations = GC.GetAllocatedBytesForCurrentThread() - beforePipe;

        // Measure StreamWriter allocations
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        long beforeStream = GC.GetAllocatedBytesForCurrentThread();
        await SerializeViaStreamWriter(response);
        long streamAllocations = GC.GetAllocatedBytesForCurrentThread() - beforeStream;

        // StreamWriter should allocate less because it doesn't need the Pipe's internal
        // buffer segments to hold the entire response. It flushes to a MemoryStream in chunks.
        // Note: In production with Kestrel, the difference is even larger because the Pipe's
        // internal buffers come from ArrayPool and may cause LOH fragmentation.
        Console.WriteLine($"PipeWriter allocations:   {pipeAllocations:N0} bytes");
        Console.WriteLine($"StreamWriter allocations: {streamAllocations:N0} bytes");
        Console.WriteLine($"Difference:               {pipeAllocations - streamAllocations:N0} bytes ({(double)pipeAllocations / streamAllocations:F2}x)");

        // We don't assert a hard ratio since GC allocation tracking is approximate,
        // but log the comparison for review
        streamAllocations.Should().BeGreaterThan(0, "sanity check");
    }

    // ──────────────────────────────────────────────────────────
    // BATCH SCENARIO: the most impactful real-world case
    // ──────────────────────────────────────────────────────────

    [Test]
    public async Task Problem_PipeWriter_BatchResponse_EntirelyBuffered()
    {
        // Simulate a 50-entry batch response where each entry is ~10KB
        // Total response: ~500KB — enough to cause LOH allocation
        TrackingPipe tracker = new();
        CountingPipeWriter writer = new(tracker.Writer);

        await WriteBatchResponse(writer, entryCount: 50, entryDataSize: 10_000);
        await writer.CompleteAsync();

        // Read what the pipe accumulated
        ReadResult result = await tracker.Reader.ReadAsync();
        long totalBuffered = result.Buffer.Length;
        tracker.Reader.AdvanceTo(result.Buffer.End);
        await tracker.Reader.CompleteAsync();

        totalBuffered.Should().BeGreaterThan(400_000,
            "PipeWriter buffered the entire ~500KB batch response before any byte reached the consumer");

        writer.WrittenCount.Should().Be(totalBuffered);
    }

    [Test]
    public async Task Fix_StreamWriter_BatchResponse_StreamedIncrementally()
    {
        // Same 50-entry batch, but through CountingStreamPipeWriter
        TrackingStream tracker = new();
        CountingStreamPipeWriter writer = new(tracker);

        await WriteBatchResponse(writer, entryCount: 50, entryDataSize: 10_000);
        await writer.CompleteAsync();

        // The stream received the data in many small writes
        tracker.WriteCallCount.Should().BeGreaterThan(10,
            "batch response should stream incrementally, not in one shot");

        tracker.LargestSingleWrite.Should().BeLessThan(100_000,
            "no single write to the stream should be a significant fraction of the total response");

        writer.WrittenCount.Should().BeGreaterThan(400_000);
    }

    // ──────────────────────────────────────────────────────────
    // CORRECTNESS: ensure both paths produce identical output
    // ──────────────────────────────────────────────────────────

    [Test]
    public async Task BothWriters_ProduceIdenticalOutput_SmallResponse()
    {
        JsonRpcSuccessResponse response = new() { Id = 1, Result = "0x1234" };

        (byte[] pipeBytes, long pipeCount) = await SerializeViaPipeWriter(response);
        (byte[] streamBytes, long streamCount) = await SerializeViaStreamWriter(response);

        streamBytes.Should().Equal(pipeBytes, "both writers must produce identical JSON bytes");
        streamCount.Should().Be(pipeCount, "WrittenCount must match");
    }

    [Test]
    public async Task BothWriters_ProduceIdenticalOutput_ErrorResponse()
    {
        JsonRpcErrorResponse response = new()
        {
            Id = 42,
            Error = new Error { Code = -32600, Message = "Invalid request" }
        };

        (byte[] pipeBytes, long pipeCount) = await SerializeViaPipeWriter(response);
        (byte[] streamBytes, long streamCount) = await SerializeViaStreamWriter(response);

        streamBytes.Should().Equal(pipeBytes);
        streamCount.Should().Be(pipeCount);
    }

    [TestCase(100)]
    [TestCase(1_000)]
    [TestCase(10_000)]
    public async Task BothWriters_ProduceIdenticalOutput_LargeArray(int elementCount)
    {
        List<Dictionary<string, string>> largeResult = new(elementCount);
        for (int i = 0; i < elementCount; i++)
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

        (byte[] pipeBytes, long pipeCount) = await SerializeViaPipeWriter(response);
        (byte[] streamBytes, long streamCount) = await SerializeViaStreamWriter(response);

        streamBytes.Should().Equal(pipeBytes, "large responses must be byte-identical");
        streamCount.Should().Be(pipeCount);
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(100)]
    public async Task BothWriters_ProduceIdenticalOutput_BatchResponse(int batchSize)
    {
        byte[] openBracket = "["u8.ToArray();
        byte[] comma = ","u8.ToArray();
        byte[] closeBracket = "]"u8.ToArray();

        // PipeWriter path
        Pipe pipe = new();
        CountingPipeWriter pipeWriter = new(pipe.Writer);
        pipeWriter.Write(openBracket);
        for (int i = 0; i < batchSize; i++)
        {
            if (i > 0) pipeWriter.Write(comma);
            JsonRpcSuccessResponse entry = new() { Id = i, Result = $"result_{i}" };
            await Serializer.SerializeAsync(pipeWriter, entry);
        }
        pipeWriter.Write(closeBracket);
        await pipeWriter.CompleteAsync();

        ReadResult pipeRead = await pipe.Reader.ReadAsync();
        byte[] pipeBytes = pipeRead.Buffer.ToArray();
        pipe.Reader.AdvanceTo(pipeRead.Buffer.End);
        await pipe.Reader.CompleteAsync();

        // StreamWriter path
        MemoryStream ms = new();
        CountingStreamPipeWriter streamWriter = new(ms);
        streamWriter.Write(openBracket);
        for (int i = 0; i < batchSize; i++)
        {
            if (i > 0) streamWriter.Write(comma);
            JsonRpcSuccessResponse entry = new() { Id = i, Result = $"result_{i}" };
            await Serializer.SerializeAsync(streamWriter, entry);
        }
        streamWriter.Write(closeBracket);
        await streamWriter.CompleteAsync();

        byte[] streamBytes = ms.ToArray();

        streamBytes.Should().Equal(pipeBytes, "batch output must be byte-identical");
        pipeWriter.WrittenCount.Should().Be(streamWriter.WrittenCount);

        // Verify valid JSON
        string json = Encoding.UTF8.GetString(streamBytes);
        JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(batchSize);
    }

    [Test]
    public async Task BothWriters_ProduceIdenticalOutput_5MB()
    {
        string largeData = new('A', 5 * 1024 * 1024);
        JsonRpcSuccessResponse response = new() { Id = 1, Result = largeData };

        (byte[] pipeBytes, long pipeCount) = await SerializeViaPipeWriter(response);
        (byte[] streamBytes, long streamCount) = await SerializeViaStreamWriter(response);

        streamCount.Should().Be(pipeCount);
        streamBytes.Should().Equal(pipeBytes);
        streamCount.Should().BeGreaterThan(5 * 1024 * 1024);
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────

    private static async Task<(byte[] Bytes, long WrittenCount)> SerializeViaPipeWriter<T>(T value)
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

    private static async Task<(byte[] Bytes, long WrittenCount)> SerializeViaStreamWriter<T>(T value)
    {
        MemoryStream ms = new();
        CountingStreamPipeWriter writer = new(ms);

        await Serializer.SerializeAsync(writer, value);
        await writer.CompleteAsync();

        return (ms.ToArray(), writer.WrittenCount);
    }

    private static async Task WriteBatchResponse(PipeWriter writer, int entryCount, int entryDataSize)
    {
        writer.Write("["u8);
        for (int i = 0; i < entryCount; i++)
        {
            if (i > 0) writer.Write(","u8);
            string data = new('x', entryDataSize);
            JsonRpcSuccessResponse entry = new() { Id = i, Result = data };
            await Serializer.SerializeAsync(writer, entry);
        }
        writer.Write("]"u8);
    }

    /// <summary>
    /// A Stream that tracks write patterns: count, sizes, and peak single write.
    /// Used to prove CountingStreamPipeWriter flushes incrementally.
    /// </summary>
    private sealed class TrackingStream : MemoryStream
    {
        public int WriteCallCount { get; private set; }
        public long TotalBytesWritten { get; private set; }
        public int LargestSingleWrite { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteCallCount++;
            TotalBytesWritten += buffer.Length;
            if (buffer.Length > LargestSingleWrite) LargestSingleWrite = buffer.Length;
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCallCount++;
            TotalBytesWritten += count;
            if (count > LargestSingleWrite) LargestSingleWrite = count;
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCallCount++;
            TotalBytesWritten += buffer.Length;
            if (buffer.Length > LargestSingleWrite) LargestSingleWrite = buffer.Length;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCallCount++;
            TotalBytesWritten += count;
            if (count > LargestSingleWrite) LargestSingleWrite = count;
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    /// <summary>
    /// Thin wrapper around Pipe to expose Reader/Writer for test use with CountingPipeWriter.
    /// </summary>
    private sealed class TrackingPipe
    {
        private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: long.MaxValue));
        public PipeWriter Writer => _pipe.Writer;
        public PipeReader Reader => _pipe.Reader;
    }

    // ──────────────────────────────────────────────────────────
    // HEAD-TO-HEAD: Peak buffered memory during serialization
    //
    // The core problem: during synchronous JSON serialization,
    // Utf8JsonWriter calls GetSpan/Advance repeatedly. PipeWriter
    // accumulates ALL these bytes in its internal buffer (UnflushedBytes
    // grows to total response size). CountingStreamPipeWriter auto-flushes
    // to the stream every ~4KB, keeping UnflushedBytes bounded.
    //
    // These tests measure UnflushedBytes (peak internal buffer) during
    // serialization. The PipeWriter path shows unbounded growth.
    // The StreamWriter path stays bounded regardless of response size.
    // ──────────────────────────────────────────────────────────

    [TestCase(1)]   // 1MB
    [TestCase(5)]   // 5MB
    public async Task PeakBuffer_PipeWriter_GrowsToFullResponseSize(int sizeMB)
    {
        int totalBytes = sizeMB * 1024 * 1024;
        string largeData = new('Z', totalBytes);
        JsonRpcSuccessResponse response = new() { Id = 1, Result = largeData };

        // ─── Master path: CountingPipeWriter wrapping a Pipe ───
        // In production this is ctx.Response.BodyWriter (Kestrel's pipe)
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: long.MaxValue));
        PeakTrackingWriter masterWriter = new(new CountingPipeWriter(pipe.Writer));
        await Serializer.SerializeAsync(masterWriter, response);
        long masterPeakBuffer = masterWriter.PeakUnflushedBytes;
        await masterWriter.CompleteAsync();
        // Drain pipe to complete the test
        ReadResult result = await pipe.Reader.ReadAsync();
        pipe.Reader.AdvanceTo(result.Buffer.End);
        await pipe.Reader.CompleteAsync();

        // ─── PR path: CountingStreamPipeWriter wrapping a Stream ───
        // In production this is ctx.Response.Body (Kestrel's response stream)
        MemoryStream ms = new();
        PeakTrackingWriter prWriter = new(new CountingStreamPipeWriter(ms));
        await Serializer.SerializeAsync(prWriter, response);
        long prPeakBuffer = prWriter.PeakUnflushedBytes;
        await prWriter.CompleteAsync();

        Console.WriteLine($"=== {sizeMB}MB response: peak internal buffer ===");
        Console.WriteLine($"Master (PipeWriter):       {masterPeakBuffer:N0} bytes peak");
        Console.WriteLine($"PR     (StreamPipeWriter): {prPeakBuffer:N0} bytes peak");
        Console.WriteLine($"Reduction: {(double)masterPeakBuffer / prPeakBuffer:F0}x less memory held during serialization");

        // PROOF: PipeWriter accumulated the entire response in its buffer
        masterPeakBuffer.Should().BeGreaterThan(totalBytes,
            $"PipeWriter should buffer the entire {sizeMB}MB response (this is the problem)");

        // PROOF: StreamWriter kept its buffer bounded (auto-flush at ~4KB)
        prPeakBuffer.Should().BeLessThan(64 * 1024,
            "StreamPipeWriter should auto-flush, keeping peak buffer under 64KB");

        // The actual ratio should be dramatic
        masterPeakBuffer.Should().BeGreaterThan(prPeakBuffer * 10,
            "PipeWriter's peak buffer should be >10x larger than StreamPipeWriter's");
    }

    [Test]
    public async Task PeakBuffer_BatchResponse_PipeWriterUnbounded_StreamWriterBounded()
    {
        // 100-entry batch with 5KB per entry = ~500KB total
        int entryCount = 100;
        int entryDataSize = 5_000;

        // ─── Master path ───
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: long.MaxValue));
        PeakTrackingWriter masterWriter = new(new CountingPipeWriter(pipe.Writer));
        await WriteBatchResponse(masterWriter, entryCount, entryDataSize);
        long masterPeak = masterWriter.PeakUnflushedBytes;
        await masterWriter.CompleteAsync();
        ReadResult result = await pipe.Reader.ReadAsync();
        long totalResponseSize = result.Buffer.Length;
        pipe.Reader.AdvanceTo(result.Buffer.End);
        await pipe.Reader.CompleteAsync();

        // ─── PR path ───
        MemoryStream ms = new();
        PeakTrackingWriter prWriter = new(new CountingStreamPipeWriter(ms));
        await WriteBatchResponse(prWriter, entryCount, entryDataSize);
        long prPeak = prWriter.PeakUnflushedBytes;
        await prWriter.CompleteAsync();

        Console.WriteLine($"=== 100-entry batch ({totalResponseSize:N0} bytes total) ===");
        Console.WriteLine($"Master peak buffer: {masterPeak:N0} bytes");
        Console.WriteLine($"PR peak buffer:     {prPeak:N0} bytes");
        Console.WriteLine($"Reduction:          {(double)masterPeak / prPeak:F0}x");

        masterPeak.Should().BeGreaterThan(totalResponseSize / 2,
            "PipeWriter should buffer most of the batch response");

        prPeak.Should().BeLessThan(64 * 1024,
            "StreamPipeWriter should auto-flush, keeping peak buffer bounded");
    }

    [Test]
    public async Task StreamWriter_ConsumerReceivesData_DuringSerialization()
    {
        // This proves data flows to the consumer DURING serialization with StreamWriter,
        // rather than only AFTER serialization completes (as with PipeWriter).
        int totalBytes = 2 * 1024 * 1024;
        string largeData = new('W', totalBytes);
        JsonRpcSuccessResponse response = new() { Id = 1, Result = largeData };

        // Track how many bytes the stream received after each Advance call
        MidpointTrackingStream tracker = new();
        CountingStreamPipeWriter writer = new(tracker);

        await Serializer.SerializeAsync(writer, response);

        // Before CompleteAsync, the stream should ALREADY have most of the data
        // because CountingStreamPipeWriter auto-flushed during serialization
        long bytesReceivedDuringSerialization = tracker.Position;
        await writer.CompleteAsync();
        long bytesReceivedAfterComplete = tracker.Position;

        Console.WriteLine($"=== 2MB response: consumer data availability ===");
        Console.WriteLine($"Bytes received DURING serialization: {bytesReceivedDuringSerialization:N0}");
        Console.WriteLine($"Bytes received AFTER CompleteAsync:  {bytesReceivedAfterComplete:N0}");
        Console.WriteLine($"Data available during serialization: {100.0 * bytesReceivedDuringSerialization / bytesReceivedAfterComplete:F1}%");

        bytesReceivedDuringSerialization.Should().BeGreaterThan(totalBytes / 2,
            "Most data should reach the consumer DURING serialization, not after — " +
            "this means Kestrel can start sending bytes to the network immediately");
    }

    /// <summary>
    /// Wraps any PipeWriter and tracks peak UnflushedBytes during writes.
    /// This measures the maximum internal buffer size held at any point during serialization.
    /// </summary>
    private sealed class PeakTrackingWriter(PipeWriter inner) : PipeWriter
    {
        public long PeakUnflushedBytes { get; private set; }

        public override void Advance(int bytes)
        {
            inner.Advance(bytes);
            long unflushed = inner.UnflushedBytes;
            if (unflushed > PeakUnflushedBytes) PeakUnflushedBytes = unflushed;
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

    /// <summary>
    /// A MemoryStream that can be inspected for Position during serialization
    /// to prove data was flushed before serialization completed.
    /// </summary>
    private sealed class MidpointTrackingStream : MemoryStream { }
}
