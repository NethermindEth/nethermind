// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

internal sealed class SocketJsonRpcResponseSink<TStream>(
    TStream stream,
    IJsonRpcLocalStats jsonRpcLocalStats,
    long? maxBatchResponseBodySize,
    SocketSendLock sendLock,
    JsonRpcContext jsonRpcContext) : IJsonRpcResponseSink, IDisposable
    where TStream : Stream, IMessageBorderPreservingStream
{
    private readonly bool _reportCalls = jsonRpcLocalStats.IsEnabled;
    private long _topLevelResponseBytes;
    private long _batchStartTimestamp;
    private bool _isFirstBatchItem = true;
    private bool _holdsSendLock;
    private CountingStreamPipeWriter? _batchWriter;

    public long BytesWritten { get; private set; }
    public bool StopRequested { get; private set; }

    public async ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        _holdsSendLock = true;

        try
        {
            long startTimestamp = _reportCalls ? Stopwatch.GetTimestamp() : 0;
            long responseBytes = await SocketJsonRpcResponseWriter.WriteMessageAsync(stream, sendLock, response, cancellationToken);
            report = JsonRpcResponseWriteOutcome.Of(response).ApplyTo(report);

            BytesWritten += responseBytes;
            if (_reportCalls)
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds;
                jsonRpcLocalStats.ReportCall(report, handlingTimeMicroseconds, responseBytes);
            }
        }
        catch
        {
            if (_reportCalls) jsonRpcLocalStats.ReportCall(report with { Success = false });
            throw;
        }
        finally
        {
            ReleaseSendLock();
        }
    }

    public async ValueTask BeginBatchAsync(CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        _holdsSendLock = true;
        _topLevelResponseBytes = 1;
        _batchStartTimestamp = _reportCalls ? Stopwatch.GetTimestamp() : 0;
        _isFirstBatchItem = true;
        StopRequested = false;

        _batchWriter = SocketJsonRpcResponseWriter.BeginBatch(stream, sendLock);
    }

    public async ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        if (!_isFirstBatchItem)
        {
            JsonRpcResponseWriter.WriteBatchSeparator(_batchWriter);
            _topLevelResponseBytes++;
        }

        _isFirstBatchItem = false;

        try
        {
            _topLevelResponseBytes += await SocketJsonRpcResponseWriter.WriteBatchItemAsync(_batchWriter, response, cancellationToken);
            report = JsonRpcResponseWriteOutcome.Of(response).ApplyTo(report);
        }
        catch (Exception ex)
        {
            // Earlier batch bytes may already be on the stream, so any item failure leaves an incomplete message.
            sendLock.Fault(ex);
            if (_reportCalls) jsonRpcLocalStats.ReportCall(report with { Success = false });
            throw;
        }
        if (_reportCalls)
        {
            jsonRpcLocalStats.ReportCall(report);
        }

        if (!jsonRpcContext.IsAuthenticated && _topLevelResponseBytes > maxBatchResponseBodySize)
        {
            StopRequested = true;
        }
    }

    public async ValueTask EndBatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonRpcResponseWriter.WriteBatchEnd(_batchWriter);
            _topLevelResponseBytes++;

            _topLevelResponseBytes += await SocketJsonRpcResponseWriter.EndMessageAsync(stream, _batchWriter, cancellationToken);
            BytesWritten += _topLevelResponseBytes;

            if (_reportCalls)
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(_batchStartTimestamp).TotalMicroseconds;
                jsonRpcLocalStats.ReportCall(new RpcReport(RpcReport.CollectionSerialization, handlingTimeMicroseconds, true), handlingTimeMicroseconds, _topLevelResponseBytes);
            }
        }
        catch (Exception ex)
        {
            sendLock.Fault(ex);
            throw;
        }
        finally
        {
            ReleaseSendLock();
        }
    }

    public void Dispose()
    {
        if (_holdsSendLock) sendLock.Fault();
        ReleaseSendLock();
    }

    private void ReleaseSendLock()
    {
        if (!_holdsSendLock)
        {
            return;
        }

        _holdsSendLock = false;
        sendLock.Release();
    }
}

internal static class SocketJsonRpcResponseWriter
{
    private static readonly StreamPipeWriterOptions ResponsePipeWriterOptions = new(minimumBufferSize: 32 * 1024, leaveOpen: true);

    /// <summary>Writes <paramref name="response"/> as one complete message.</summary>
    /// <remarks>
    /// The caller must hold <paramref name="sendLock"/>. A failure once bytes may have reached the stream faults it,
    /// because the incomplete message can no longer be finished; a failure before that leaves the connection usable.
    /// </remarks>
    public static async ValueTask<long> WriteMessageAsync<TStream>(TStream stream, SocketSendLock sendLock, JsonRpcResponse response, CancellationToken cancellationToken)
        where TStream : Stream, IMessageBorderPreservingStream
    {
        CountingStreamPipeWriter writer = await WriteAsync(stream, sendLock, response, cancellationToken);
        long responseBytes = writer.WrittenCount;
        try
        {
            return responseBytes + await EndMessageAsync(stream, writer, cancellationToken);
        }
        catch (Exception ex)
        {
            sendLock.Fault(ex);
            throw;
        }
    }

    /// <summary>Writes <paramref name="response"/> without ending the message.</summary>
    /// <remarks>
    /// The caller must hold <paramref name="sendLock"/>, which is faulted when the failure follows the start of a stream
    /// write. A failed write counts: a cancelled socket send can leave part of the message on an open connection.
    /// </remarks>
    private static async ValueTask<CountingStreamPipeWriter> WriteAsync(Stream stream, SocketSendLock sendLock, JsonRpcResponse response, CancellationToken cancellationToken)
    {
        CountingStreamPipeWriter writer = sendLock.GetWriter(stream, ResponsePipeWriterOptions, 0);
        try
        {
            await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, cancellationToken);
            return writer;
        }
        catch (Exception ex)
        {
            if (writer.MayHaveWrittenToStream) sendLock.Fault(ex);
            writer.Reset(0);
            throw;
        }
    }

    /// <summary>Ends the message with what <paramref name="writer"/> still buffers, passing its last segment to the stream without a copy.</summary>
    /// <returns>The number of bytes the stream added to end the message.</returns>
    public static async ValueTask<int> EndMessageAsync<TStream>(TStream stream, CountingStreamPipeWriter writer, CancellationToken cancellationToken)
        where TStream : Stream, IMessageBorderPreservingStream
    {
        ReadOnlyMemory<byte> last = await writer.FlushAllButLastSegmentAsync(cancellationToken);
        int written = await stream.WriteEndOfMessageAsync(last, cancellationToken);
        writer.Reset(0);
        return written;
    }

    /// <summary>Returns the connection's response writer holding the opening of a batch response. The caller must hold <paramref name="sendLock"/>.</summary>
    public static CountingStreamPipeWriter BeginBatch(Stream stream, SocketSendLock sendLock)
    {
        CountingStreamPipeWriter writer = sendLock.GetWriter(stream, ResponsePipeWriterOptions, 0);
        JsonRpcResponseWriter.WriteBatchStart(writer);
        return writer;
    }

    /// <summary>Writes a batch item into <paramref name="writer"/>, first flushing what precedes it once that fills the buffer.</summary>
    /// <returns>The number of bytes the item added.</returns>
    public static async ValueTask<long> WriteBatchItemAsync(CountingStreamPipeWriter writer, JsonRpcResponse response, CancellationToken cancellationToken)
    {
        if (writer.UnflushedBytes >= ResponsePipeWriterOptions.MinimumBufferSize) await writer.FlushAsync(cancellationToken);
        long start = writer.WrittenCount;
        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, isBatch: true, cancellationToken);
        return writer.WrittenCount - start;
    }
}
