// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.Core.Resettables;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner.JsonRpc;

internal sealed class HttpJsonRpcResponseSink(
    HttpContext context,
    JsonRpcUrl jsonRpcUrl,
    IJsonRpcConfig jsonRpcConfig,
    IJsonRpcLocalStats jsonRpcLocalStats,
    ILogger logger,
    long requestStartTimestamp) : IJsonRpcResponseSink
{
    private static ReadOnlySpan<byte> JsonOpeningBracket => [(byte)'['];
    private static ReadOnlySpan<byte> JsonComma => [(byte)','];
    private static ReadOnlySpan<byte> JsonClosingBracket => [(byte)']'];
    private const string JsonContentType = "application/json";
    private const int BufferedResponseInitialCapacity = 16 * 1024;
    private static readonly StringValues JsonContentTypeHeader = new(JsonContentType);
    private static readonly StreamPipeWriterOptions BufferedResponsePipeWriterOptions =
        new(minimumBufferSize: BufferedResponseInitialCapacity, leaveOpen: true);

    private readonly bool _reportCalls = jsonRpcLocalStats.IsEnabled;
    private CountingWriter? _writer;
    private Stream? _bufferedStream;
    private bool _isFirstBatchItem = true;
    private bool _completed;

    public long BytesWritten => _writer?.WrittenCount ?? 0;
    public bool StopRequested { get; private set; }

    public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        EnsureStarted(isCollection: false, response);
        return WriteSingleStartedAsync(response, report, cancellationToken);
    }

    private ValueTask WriteSingleStartedAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        ValueTask writeTask = WriteResponseAsync(_writer!, response, cancellationToken);
        if (!writeTask.IsCompletedSuccessfully)
        {
            return WriteSingleAfterWriteAsync(writeTask, report);
        }

        writeTask.GetAwaiter().GetResult();
        ReportSingle(report);
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteSingleAfterWriteAsync(ValueTask writeTask, RpcReport report)
    {
        await writeTask;
        ReportSingle(report);
    }

    private void ReportSingle(RpcReport report)
    {
        if (!_reportCalls)
        {
            return;
        }

        long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(requestStartTimestamp).TotalMicroseconds;
        jsonRpcLocalStats.ReportCall(report, handlingTimeMicroseconds, BytesWritten);
    }

    public ValueTask BeginBatchAsync(CancellationToken cancellationToken)
    {
        EnsureStarted(isCollection: true, response: null);
        _writer!.Write(JsonOpeningBracket);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        if (!_isFirstBatchItem)
        {
            _writer!.Write(JsonComma);
        }

        _isFirstBatchItem = false;

        ValueTask writeTask = WriteResponseAsync(_writer!, response, cancellationToken);
        if (!writeTask.IsCompletedSuccessfully)
        {
            return WriteBatchItemAfterWriteAsync(writeTask, report);
        }

        writeTask.GetAwaiter().GetResult();
        ReportBatchItem(report);
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteBatchItemAfterWriteAsync(ValueTask writeTask, RpcReport report)
    {
        await writeTask;
        ReportBatchItem(report);
    }

    private void ReportBatchItem(RpcReport report)
    {
        if (_reportCalls)
        {
            jsonRpcLocalStats.ReportCall(report);
        }

        if (!jsonRpcUrl.IsAuthenticated && BytesWritten > jsonRpcConfig.MaxBatchResponseBodySize)
        {
            if (logger.IsWarn) logger.Warn($"The max batch response body size exceeded. The current response size {BytesWritten}, and the config setting is JsonRpc.{nameof(jsonRpcConfig.MaxBatchResponseBodySize)} = {jsonRpcConfig.MaxBatchResponseBodySize}");
            StopRequested = true;
        }
    }

    public ValueTask EndBatchAsync(CancellationToken cancellationToken)
    {
        _writer!.Write(JsonClosingBracket);

        if (_reportCalls)
        {
            long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(requestStartTimestamp).TotalMicroseconds;
            jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, BytesWritten);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed || _writer is null)
        {
            return ValueTask.CompletedTask;
        }

        _completed = true;
        ValueTask writerCompleteTask = _writer.CompleteAsync();
        if (!writerCompleteTask.IsCompletedSuccessfully)
        {
            return CompleteAfterWriterAsync(writerCompleteTask, cancellationToken);
        }

        writerCompleteTask.GetAwaiter().GetResult();
        return CompleteResponseAsync(cancellationToken);
    }

    private async ValueTask CompleteAfterWriterAsync(ValueTask writerCompleteTask, CancellationToken cancellationToken)
    {
        await writerCompleteTask;
        await CompleteResponseAsync(cancellationToken);
    }

    private ValueTask CompleteResponseAsync(CancellationToken cancellationToken)
    {
        if (_bufferedStream is not null)
        {
            return CompleteBufferedResponseAsync(cancellationToken);
        }

        Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, BytesWritten);
        Task completeTask = context.Response.CompleteAsync();
        if (!completeTask.IsCompletedSuccessfully)
        {
            return new ValueTask(completeTask);
        }

        completeTask.GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }

    private async ValueTask CompleteBufferedResponseAsync(CancellationToken cancellationToken)
    {
        Stream bufferedStream = _bufferedStream!;
        _bufferedStream = null;

        try
        {
            context.Response.ContentLength = BytesWritten;
            bufferedStream.Seek(0, SeekOrigin.Begin);
            await bufferedStream.CopyToAsync(context.Response.Body, cancellationToken);
        }
        finally
        {
            await bufferedStream.DisposeAsync();
        }

        Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, BytesWritten);
        await context.Response.CompleteAsync();
    }

    private void EnsureStarted(bool isCollection, JsonRpcResponse? response)
    {
        if (_writer is not null)
        {
            return;
        }

        bool bufferResponse = jsonRpcConfig.BufferResponses && !(jsonRpcUrl.IsAuthenticated && !isCollection);
        _bufferedStream = bufferResponse ? RecyclableStream.GetStream("http", BufferedResponseInitialCapacity) : null;
        _writer = _bufferedStream is not null ? new CountingStreamPipeWriter(_bufferedStream, BufferedResponsePipeWriterOptions) : new CountingPipeWriter(context.Response.BodyWriter);

        context.Response.Headers.ContentType = JsonContentTypeHeader;
        context.Response.StatusCode = isCollection
            ? StatusCodes.Status200OK
            : GetStatusCode(response);

        return;
    }

    private static int GetStatusCode(JsonRpcResponse? response) =>
        JsonRpcResponseWriter.IsResourceUnavailableError(response)
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

    private ValueTask WriteResponseAsync(CountingWriter writer, JsonRpcResponse response, CancellationToken cancellationToken)
    {
        if (JsonRpcResponseWriter.TryGetStreamableResult(response, out IStreamableResult? streamable))
        {
            return JsonRpcResponseWriter.WriteStreamableAsync(writer, response, streamable, cancellationToken);
        }

        JsonRpcResponseWriter.Write(writer, response, EthereumJsonSerializer.JsonOptions);
        return ValueTask.CompletedTask;
    }
}
