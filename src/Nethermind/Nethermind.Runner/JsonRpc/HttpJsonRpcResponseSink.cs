// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.IO;
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
    long requestStartTimestamp,
    UnsentResponseCounter? unsentResponses = null) : IJsonRpcResponseSink
{
    private const string JsonContentType = "application/json";
    private const int BufferedResponseInitialCapacity = 16 * 1024;
    private static readonly StringValues JsonContentTypeHeader = new(JsonContentType);

    private readonly bool _reportCalls = jsonRpcLocalStats.IsEnabled;
    private CountingWriter? _writer;
    private RecyclableMemoryStream? _bufferedStream;
    private bool _isFirstBatchItem = true;
    private bool _completed;
    private long _accountedBytes;
    private long _reservedBytes;
    private bool _responseCounted;
    private UnsentResponseCounter.CallerKey _caller;
    private bool _callerResolved;
    private IPAddress? _clientAddress;
    private bool _forwarded;
    private bool _overLimit;

    public long BytesWritten => _writer?.WrittenCount ?? 0;
    public bool StopRequested { get; private set; }

    public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        EnsureStarted(isCollection: false, response);
        return WriteStartedAsync(response, report, isBatch: false, cancellationToken);
    }

    private ValueTask WriteStartedAsync(JsonRpcResponse response, RpcReport report, bool isBatch, CancellationToken cancellationToken)
    {
        bool keepsBytes = _bufferedStream is not null || !response.TryGetStreamableResult(out _);
        ValueTask writeTask;
        try
        {
            // A non-streamed response is serialized synchronously and can fail before a task exists.
            writeTask = JsonRpcResponseWriter.WriteAsync(_writer!, response, EthereumJsonSerializer.JsonOptions, isBatch, cancellationToken);
        }
        catch
        {
            ReportWrite(report with { Success = false }, isBatch);
            throw;
        }

        if (writeTask.IsCompletedSuccessfully)
        {
            writeTask.GetAwaiter().GetResult();
            ReserveWrittenBytes(keepsBytes, isBatch, report.Method);
            CompleteWrite(response, report, isBatch);
            return ValueTask.CompletedTask;
        }

        // Only a deferred result can be replaced while it is written, so any other response is reported as is and
        // its continuation does not need to keep the response.
        return response.Streaming is null
            ? WriteAfterWriteAsync(writeTask, JsonRpcResponseWriteOutcome.Of(response).ApplyTo(report), isBatch, keepsBytes)
            : WriteDeferredAfterWriteAsync(writeTask, response, report, isBatch, keepsBytes);
    }

    private async ValueTask WriteAfterWriteAsync(ValueTask writeTask, RpcReport report, bool isBatch, bool keepsBytes)
    {
        try
        {
            await writeTask;
        }
        catch
        {
            ReportWrite(report with { Success = false }, isBatch);
            throw;
        }
        ReserveWrittenBytes(keepsBytes, isBatch, report.Method);
        ReportWrite(report, isBatch);
    }

    private async ValueTask WriteDeferredAfterWriteAsync(ValueTask writeTask, JsonRpcResponse response, RpcReport report, bool isBatch, bool keepsBytes)
    {
        try
        {
            await writeTask;
        }
        catch
        {
            ReportWrite(report with { Success = false }, isBatch);
            throw;
        }
        ReserveWrittenBytes(keepsBytes, isBatch, report.Method);
        CompleteWrite(response, report, isBatch);
    }

    private void ReserveWrittenBytes(bool keepsBytes, bool isBatch, string method)
    {
        long written = BytesWritten;
        long bytes = written - _accountedBytes;
        _accountedBytes = written;
        if (unsentResponses is null || _overLimit)
        {
            return;
        }

        if (keepsBytes && bytes > 0 && !TryReserve(bytes, written, isBatch, method))
        {
            return;
        }

        ReleaseFlushedBytes();
    }

    private bool TryReserve(long bytes, long written, bool isBatch, string method)
    {
        if (!_callerResolved)
        {
            _caller = unsentResponses!.ResolveCaller(context, out _clientAddress, out _forwarded);
            _callerResolved = true;
        }

        long limit = jsonRpcConfig.MaxUnsentResponsesSize;
        UnsentResponseLimit hitLimit = unsentResponses!.TryReserve(_caller, _reservedBytes, bytes, _responseCounted, limit, jsonRpcConfig.MaxUnsentResponsesPerAddress, GetBatchLimitAllowance(isBatch, limit));
        if (hitLimit == UnsentResponseLimit.None)
        {
            _reservedBytes += bytes;
            _responseCounted |= UnsentResponseCounter.IsCounted(_reservedBytes);
            return true;
        }

        ReportLimitExceeded(hitLimit, written, method);
        _overLimit = true;
        StopRequested = true;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReportLimitExceeded(UnsentResponseLimit hitLimit, long written, string method)
    {
        Metrics.JsonRpcHttpUnsentLimitAborts.AddOrUpdate(GetMetricLabel(hitLimit), 1, static (_, count) => count + 1);
        if (!logger.IsWarn || !unsentResponses!.TryStartWarning(out long suppressed)) return;

        string limit = hitLimit switch
        {
            UnsentResponseLimit.Node => $"the node limit JsonRpc.{nameof(jsonRpcConfig.MaxUnsentResponsesSize)} = {jsonRpcConfig.MaxUnsentResponsesSize} bytes",
            UnsentResponseLimit.AddressSize => $"the share of one address, 1/{UnsentResponseCounter.CallerShareDivisor} of JsonRpc.{nameof(jsonRpcConfig.MaxUnsentResponsesSize)} = {jsonRpcConfig.MaxUnsentResponsesSize} bytes",
            _ => $"JsonRpc.{nameof(jsonRpcConfig.MaxUnsentResponsesPerAddress)} = {jsonRpcConfig.MaxUnsentResponsesPerAddress} responses over 1 MiB"
        };
        string caller = _clientAddress is null ? $"connection {context.Connection.Id}"
            : _forwarded ? $"{_clientAddress} via {context.Connection.RemoteIpAddress}"
            : _clientAddress.ToString();
        string skipped = suppressed > 0 ? $" {suppressed} more were closed since the last warning." : string.Empty;
        logger.Warn($"Closed the HTTP JSON-RPC connection of {caller}: the response to {method} of {written} bytes would take its unsent responses above {limit}.{skipped}");
    }

    private static string GetMetricLabel(UnsentResponseLimit hitLimit) => hitLimit switch
    {
        UnsentResponseLimit.Node => "node",
        UnsentResponseLimit.AddressSize => "address_size",
        _ => "address_responses"
    };

    private void ReleaseFlushedBytes()
    {
        if (_reservedBytes == 0 || _writer is not CountingPipeWriter writer)
        {
            return;
        }

        long flushed = _reservedBytes - (writer.WrittenCount - writer.FlushedCount);
        if (flushed <= 0)
        {
            return;
        }

        _reservedBytes -= flushed;
        bool uncounted = _responseCounted && !UnsentResponseCounter.IsCounted(_reservedBytes);
        unsentResponses!.Release(_caller, flushed, uncounted);
        _responseCounted &= !uncounted;
    }

    private long GetBatchLimitAllowance(bool isBatch, long limit) =>
        isBatch
        && jsonRpcConfig.MaxBatchResponseBodySize is long batchLimit
        && batchLimit > 0
        && batchLimit <= UnsentResponseCounter.CallerLimit(limit)
            ? batchLimit
            : 0;

    private void ReleaseReservedBytes()
    {
        if (_reservedBytes == 0)
        {
            return;
        }

        unsentResponses!.Release(_caller, _reservedBytes, _responseCounted);
        _reservedBytes = 0;
        _responseCounted = false;
    }

    private void CompleteWrite(JsonRpcResponse response, RpcReport report, bool isBatch)
    {
        JsonRpcResponseWriteOutcome outcome = JsonRpcResponseWriteOutcome.Of(response);
        if (!isBatch && !context.Response.HasStarted && outcome.IsResourceUnavailable)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        ReportWrite(outcome.ApplyTo(report), isBatch);
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

        if (_overLimit)
        {
            Abort(abortTransport: true);
            return ValueTask.CompletedTask;
        }

        _completed = true;
        if (_bufferedStream is null &&
            HttpProtocol.IsHttp11(context.Request.Protocol) &&
            !context.Response.HasStarted)
        {
            context.Response.ContentLength = BytesWritten;
        }

        ValueTask writerCompleteTask = _writer.CompleteAsync();
        ValueTask completion = writerCompleteTask.IsCompletedSuccessfully
            ? CompleteResponseAsync(cancellationToken)
            : CompleteAfterWriterAsync(writerCompleteTask, cancellationToken);
        if (completion.IsCompleted || _reservedBytes == 0)
        {
            ReleaseReservedBytes();
            return completion;
        }

        return ReleaseAfterAsync(completion);
    }

    private async ValueTask ReleaseAfterAsync(ValueTask completion)
    {
        try
        {
            await completion;
        }
        finally
        {
            ReleaseReservedBytes();
        }
    }

    /// <summary>Discards an incomplete response and optionally aborts the transport.</summary>
    public void Abort(bool abortTransport)
    {
        if (_completed) return;
        _completed = true;
        ReleaseReservedBytes();
        if (abortTransport) context.Abort();

        if (_bufferedStream is not null)
        {
            try
            {
                _writer!.Complete();
            }
            finally
            {
                _bufferedStream.Dispose();
                _bufferedStream = null;
            }
        }
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
        _writer = _bufferedStream is not null ? new RewindableStreamPipeWriter(_bufferedStream) : new CountingPipeWriter(context.Response.BodyWriter);

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
