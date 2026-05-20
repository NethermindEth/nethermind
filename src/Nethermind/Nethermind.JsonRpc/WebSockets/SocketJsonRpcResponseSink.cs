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
    private static readonly byte[] JsonOpeningBracket = [Convert.ToByte('[')];
    private static readonly byte[] JsonComma = [Convert.ToByte(',')];
    private static readonly byte[] JsonClosingBracket = [Convert.ToByte(']')];
    private static readonly StreamPipeWriterOptions ResponsePipeWriterOptions = new(leaveOpen: true);

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
            long responseBytes = await WriteResponseAsync(response, cancellationToken);
            responseBytes += await stream.WriteEndOfMessageAsync();

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

        await stream.WriteAsync(JsonOpeningBracket, cancellationToken);
    }

    public async ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        if (!_isFirstBatchItem)
        {
            await stream.WriteAsync(JsonComma, cancellationToken);
            _topLevelResponseBytes++;
        }

        _isFirstBatchItem = false;

        _topLevelResponseBytes += await WriteResponseAsync(response, cancellationToken);
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
            await stream.WriteAsync(JsonClosingBracket, cancellationToken);
            _topLevelResponseBytes++;

            _topLevelResponseBytes += await stream.WriteEndOfMessageAsync();
            BytesWritten += _topLevelResponseBytes;

            if (_reportCalls)
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(_batchStartTimestamp).TotalMicroseconds;
                jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, _topLevelResponseBytes);
            }
        }
        finally
        {
            ReleaseSemaphore();
        }
    }

    public void Dispose() => ReleaseSemaphore();

    private async ValueTask<long> WriteResponseAsync(JsonRpcResponse response, CancellationToken cancellationToken)
    {
        CountingStreamPipeWriter writer = new(stream, ResponsePipeWriterOptions);
        try
        {
            if (JsonRpcResponseWriter.TryGetStreamableResult(response, out IStreamableResult? streamable))
            {
                await JsonRpcResponseWriter.WriteStreamableAsync(writer, response, streamable, cancellationToken);
            }
            else
            {
                JsonRpcResponseWriter.Write(writer, response, EthereumJsonSerializer.JsonOptions);
            }

            await writer.FlushAsync(cancellationToken);
            return writer.WrittenCount;
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

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
