// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        return WriteStartedAsync(response, report, isBatch: false, cancellationToken);
    }

    private ValueTask WriteStartedAsync(JsonRpcResponse response, RpcReport report, bool isBatch, CancellationToken cancellationToken)
    {
        ValueTask writeTask = JsonRpcResponseWriter.WriteAsync(_writer!, response, EthereumJsonSerializer.JsonOptions, isBatch, cancellationToken);
        if (!writeTask.IsCompletedSuccessfully)
        {
            return WriteAfterWriteAsync(writeTask, report, isBatch);
        }

        writeTask.GetAwaiter().GetResult();
        ReportWrite(report, isBatch);
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteAfterWriteAsync(ValueTask writeTask, RpcReport report, bool isBatch)
    {
        await writeTask;
        ReportWrite(report, isBatch);
    }

    private void ReportWrite(RpcReport report, bool isBatch)
    {
        if (isBatch)
        {
            ReportBatchItem(report);
        }
        else
        {
            ReportSingle(report);
        }
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
        JsonRpcResponseWriter.WriteBatchStart(_writer!);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        if (!_isFirstBatchItem)
        {
            JsonRpcResponseWriter.WriteBatchSeparator(_writer!);
        }

        _isFirstBatchItem = false;
        return WriteStartedAsync(response, report, isBatch: true, cancellationToken);
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
        JsonRpcResponseWriter.WriteBatchEnd(_writer!);

        if (_reportCalls)
        {
            long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(requestStartTimestamp).TotalMicroseconds;
            jsonRpcLocalStats.ReportCall(new RpcReport(RpcReport.CollectionSerialization, handlingTimeMicroseconds, true), handlingTimeMicroseconds, BytesWritten);
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
        return writerCompleteTask.IsCompletedSuccessfully
            ? CompleteResponseAsync(cancellationToken)
            : CompleteAfterWriterAsync(writerCompleteTask, cancellationToken);
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
        return completeTask.IsCompletedSuccessfully
            ? ValueTask.CompletedTask
            : new ValueTask(completeTask);
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
    }

    private static int GetStatusCode(JsonRpcResponse? response) =>
        response switch
        {
            _ when JsonRpcResponseWriter.IsResourceUnavailableError(response) => StatusCodes.Status503ServiceUnavailable,
            JsonRpcErrorResponse { Error: { Code: ErrorCodes.ParseError } } => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status200OK
        };
}
