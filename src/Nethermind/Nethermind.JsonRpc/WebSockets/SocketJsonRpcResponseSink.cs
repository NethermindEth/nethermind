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
    SemaphoreSlim sendSemaphore,
    JsonRpcContext jsonRpcContext) : IJsonRpcResponseSink, IDisposable
    where TStream : Stream, IMessageBorderPreservingStream
{
    private readonly bool _reportCalls = jsonRpcLocalStats.IsEnabled;
    private long _topLevelResponseBytes;
    private long _batchStartTimestamp;
    private bool _isFirstBatchItem = true;
    private bool _holdsSemaphore;

    public long BytesWritten { get; private set; }
    public bool StopRequested { get; private set; }

    public async ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        await sendSemaphore.WaitAsync(cancellationToken);
        _holdsSemaphore = true;

        try
        {
            long startTimestamp = _reportCalls ? Stopwatch.GetTimestamp() : 0;
            long responseBytes = await SocketJsonRpcResponseWriter.WriteMessageAsync(stream, response, cancellationToken);

            BytesWritten += responseBytes;
            if (_reportCalls)
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds;
                jsonRpcLocalStats.ReportCall(report, handlingTimeMicroseconds, responseBytes);
            }
        }
        finally
        {
            ReleaseSemaphore();
        }
    }

    public async ValueTask BeginBatchAsync(CancellationToken cancellationToken)
    {
        await sendSemaphore.WaitAsync(cancellationToken);
        _holdsSemaphore = true;
        _topLevelResponseBytes = 1;
        _batchStartTimestamp = _reportCalls ? Stopwatch.GetTimestamp() : 0;
        _isFirstBatchItem = true;
        StopRequested = false;

        await JsonRpcResponseWriter.WriteBatchStartAsync(stream, cancellationToken);
    }

    public async ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        if (!_isFirstBatchItem)
        {
            await JsonRpcResponseWriter.WriteBatchSeparatorAsync(stream, cancellationToken);
            _topLevelResponseBytes++;
        }

        _isFirstBatchItem = false;

        _topLevelResponseBytes += await SocketJsonRpcResponseWriter.WriteAsync(stream, response, isBatch: true, _topLevelResponseBytes, cancellationToken);
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
            await JsonRpcResponseWriter.WriteBatchEndAsync(stream, cancellationToken);
            _topLevelResponseBytes++;

            _topLevelResponseBytes += await stream.WriteEndOfMessageAsync();
            BytesWritten += _topLevelResponseBytes;

            if (_reportCalls)
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(_batchStartTimestamp).TotalMicroseconds;
                jsonRpcLocalStats.ReportCall(new RpcReport(RpcReport.CollectionSerialization, handlingTimeMicroseconds, true), handlingTimeMicroseconds, _topLevelResponseBytes);
            }
        }
        finally
        {
            ReleaseSemaphore();
        }
    }

    public void Dispose() => ReleaseSemaphore();

    private void ReleaseSemaphore()
    {
        if (!_holdsSemaphore)
        {
            return;
        }

        _holdsSemaphore = false;
        sendSemaphore.Release();
    }
}

internal static class SocketJsonRpcResponseWriter
{
    private static readonly StreamPipeWriterOptions ResponsePipeWriterOptions = new(minimumBufferSize: 32 * 1024, leaveOpen: true);

    public static async ValueTask<long> WriteMessageAsync<TStream>(TStream stream, JsonRpcResponse response, CancellationToken cancellationToken)
        where TStream : Stream, IMessageBorderPreservingStream
    {
        long responseBytes = await WriteAsync(stream, response, isBatch: false, initialWrittenCount: 0, cancellationToken);
        return responseBytes + await stream.WriteEndOfMessageAsync();
    }

    public static ValueTask<long> WriteAsync(Stream stream, JsonRpcResponse response, CancellationToken cancellationToken) =>
        WriteAsync(stream, response, isBatch: false, initialWrittenCount: 0, cancellationToken);

    public static async ValueTask<long> WriteAsync(Stream stream, JsonRpcResponse response, bool isBatch, long initialWrittenCount, CancellationToken cancellationToken)
    {
        CountingStreamPipeWriter writer = new(stream, ResponsePipeWriterOptions, initialWrittenCount);
        try
        {
            await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, isBatch, cancellationToken);
            await writer.FlushAsync(cancellationToken);
            return writer.WrittenCount - initialWrittenCount;
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }
}
