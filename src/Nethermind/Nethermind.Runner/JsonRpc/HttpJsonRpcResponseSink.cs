// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.Core.Extensions;
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
    JsonSerializerOptions jsonOptions,
    ILogger logger,
    long requestStartTimestamp) : IJsonRpcResponseSink
{
    private static ReadOnlySpan<byte> JsonOpeningBracket => [(byte)'['];
    private static ReadOnlySpan<byte> JsonComma => [(byte)','];
    private static ReadOnlySpan<byte> JsonClosingBracket => [(byte)']'];
    private static ReadOnlySpan<byte> JsonRpcHexStringResultPrefix => "{\"jsonrpc\":\"2.0\",\"result\":\""u8;
    private const string JsonContentType = "application/json";
    private const int BufferedResponseInitialCapacity = 16 * 1024;
    private static readonly StringValues JsonContentTypeHeader = new(JsonContentType);
    private static readonly StreamPipeWriterOptions BufferedResponsePipeWriterOptions =
        new(minimumBufferSize: BufferedResponseInitialCapacity, leaveOpen: true);
    private static readonly ConcurrentDictionary<Type, JsonTypeInfo> _jsonTypeInfoCache = new();

    private CountingWriter? _writer;
    private Stream? _bufferedStream;
    private bool _isFirstBatchItem = true;
    private bool _completed;
    private long _reportedFlushCount;
    private long _reportedFlushTimeMicroseconds;

    public long BytesWritten => _writer?.WrittenCount ?? 0;
    public bool StopRequested { get; private set; }

    public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        long responseWriteStartTimestamp = Stopwatch.GetTimestamp();
        EnsureStarted(isCollection: false, response);
        return WriteSingleStartedAsync(response, report, responseWriteStartTimestamp, cancellationToken);
    }

    private ValueTask WriteSingleStartedAsync(JsonRpcResponse response, RpcReport report, long responseWriteStartTimestamp, CancellationToken cancellationToken)
    {
        ValueTask writeTask = WriteResponseAsync(_writer!, response, report, cancellationToken);
        if (!writeTask.IsCompletedSuccessfully)
        {
            return WriteSingleAfterWriteAsync(writeTask, report, responseWriteStartTimestamp);
        }

        writeTask.GetAwaiter().GetResult();
        ReportSingle(report, responseWriteStartTimestamp);
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteSingleAfterWriteAsync(ValueTask writeTask, RpcReport report, long responseWriteStartTimestamp)
    {
        await writeTask;
        ReportSingle(report, responseWriteStartTimestamp);
    }

    private void ReportSingle(RpcReport report, long responseWriteStartTimestamp)
    {
        report = report with
        {
            BoundaryTimings = GetResponseWriteTimings(report, responseWriteStartTimestamp)
        };

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
        long responseWriteStartTimestamp = Stopwatch.GetTimestamp();
        if (!_isFirstBatchItem)
        {
            _writer!.Write(JsonComma);
        }

        _isFirstBatchItem = false;

        ValueTask writeTask = WriteResponseAsync(_writer!, response, report, cancellationToken);
        if (!writeTask.IsCompletedSuccessfully)
        {
            return WriteBatchItemAfterWriteAsync(writeTask, report, responseWriteStartTimestamp);
        }

        writeTask.GetAwaiter().GetResult();
        ReportBatchItem(report, responseWriteStartTimestamp);
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteBatchItemAfterWriteAsync(ValueTask writeTask, RpcReport report, long responseWriteStartTimestamp)
    {
        await writeTask;
        ReportBatchItem(report, responseWriteStartTimestamp);
    }

    private void ReportBatchItem(RpcReport report, long responseWriteStartTimestamp)
    {
        report = report with
        {
            BoundaryTimings = GetResponseWriteTimings(report, responseWriteStartTimestamp)
        };

        jsonRpcLocalStats.ReportCall(report);

        if (!jsonRpcUrl.IsAuthenticated && BytesWritten > jsonRpcConfig.MaxBatchResponseBodySize)
        {
            if (logger.IsWarn) logger.Warn($"The max batch response body size exceeded. The current response size {BytesWritten}, and the config setting is JsonRpc.{nameof(jsonRpcConfig.MaxBatchResponseBodySize)} = {jsonRpcConfig.MaxBatchResponseBodySize}");
            StopRequested = true;
        }
    }

    private RpcBoundaryTimings GetResponseWriteTimings(RpcReport report, long responseWriteStartTimestamp)
    {
        long responseFlushCount = 0;
        long responseFlushMicroseconds = 0;
        if (_writer is not null)
        {
            responseFlushCount = _writer.FlushCount - _reportedFlushCount;
            responseFlushMicroseconds = _writer.FlushTimeMicroseconds - _reportedFlushTimeMicroseconds;
            _reportedFlushCount = _writer.FlushCount;
            _reportedFlushTimeMicroseconds = _writer.FlushTimeMicroseconds;
        }

        return report.BoundaryTimings.WithResponseWrite(
            (long)Stopwatch.GetElapsedTime(responseWriteStartTimestamp).TotalMicroseconds,
            responseFlushMicroseconds,
            responseFlushCount);
    }

    public ValueTask EndBatchAsync(CancellationToken cancellationToken)
    {
        _writer!.Write(JsonClosingBracket);

        long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(requestStartTimestamp).TotalMicroseconds;
        jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, BytesWritten);

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
        if (_bufferedStream is null)
        {
            Interlocked.Increment(ref Metrics.JsonRpcHttpUnbufferedResponses);
        }
        else
        {
            Interlocked.Increment(ref Metrics.JsonRpcHttpBufferedResponses);
        }

        context.Response.Headers.ContentType = JsonContentTypeHeader;
        context.Response.StatusCode = isCollection
            ? StatusCodes.Status200OK
            : GetStatusCode(response);

        return;
    }

    private static int GetStatusCode(JsonRpcResponse? response) =>
        IsResourceUnavailableError(response)
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

    private static bool IsResourceUnavailableError(JsonRpcResponse? response) => response is JsonRpcErrorResponse { Error.Code: ErrorCodes.ModuleTimeout }
        or JsonRpcErrorResponse { Error.Code: ErrorCodes.LimitExceeded };

    private ValueTask WriteResponseAsync(CountingWriter writer, JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        if (response is JsonRpcSuccessResponse { Result: IStreamableResult streamable })
        {
            Interlocked.Increment(ref Metrics.JsonRpcHttpStreamableResponses);
            return WriteStreamableResponseAsync(writer, response, streamable, cancellationToken);
        }

        Interlocked.Increment(ref Metrics.JsonRpcHttpSerializedResponses);
        if (TryWriteTrustedHexStringResponse(writer, response, report.Method))
        {
            return ValueTask.CompletedTask;
        }

        WriteJsonRpcResponse(writer, response);
        return ValueTask.CompletedTask;
    }

    private static bool TryWriteTrustedHexStringResponse(PipeWriter writer, JsonRpcResponse response, string method)
    {
        if (method != "eth_call" ||
            response is not JsonRpcSuccessResponse { Result: string value })
        {
            return false;
        }

        if (value.Length < 2 || value[0] != '0' || value[1] != 'x')
        {
            return false;
        }

        ReadOnlySpan<byte> prefix = JsonRpcHexStringResultPrefix;
        Span<byte> buffer = writer.GetSpan(prefix.Length + value.Length);
        prefix.CopyTo(buffer);
        buffer[prefix.Length] = (byte)'0';
        buffer[prefix.Length + 1] = (byte)'x';
        if (!HexConverter.TryCopyHexToUtf8(
                value.AsSpan(2),
                buffer.Slice(prefix.Length + 2, value.Length - 2)))
        {
            return false;
        }

        writer.Advance(prefix.Length + value.Length);
        writer.Write("\",\"id\":"u8);
        WriteIdRaw(writer, response.Id);
        writer.Write("}"u8);
        return true;
    }

    /// <summary>
    /// Writes a JSON-RPC response with typed serialization for the result/error payload,
    /// avoiding polymorphic dispatch through the JsonRpcResponse base class hierarchy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteJsonRpcResponse(IBufferWriter<byte> writer, JsonRpcResponse response)
    {
        using Utf8JsonWriter jsonWriter = new(writer, new JsonWriterOptions { SkipValidation = true });

        jsonWriter.WriteStartObject();
        jsonWriter.WriteString("jsonrpc"u8, "2.0"u8);

        if (response is JsonRpcSuccessResponse successResponse)
        {
            jsonWriter.WritePropertyName("result"u8);
            object? result = successResponse.Result;
            if (result is not null)
            {
                if (!TryWriteSimpleResult(jsonWriter, result))
                {
                    JsonSerializer.Serialize(jsonWriter, result, GetJsonTypeInfo(result.GetType()));
                }
            }
            else
            {
                jsonWriter.WriteNullValue();
            }
        }
        else if (response is JsonRpcErrorResponse errorResponse)
        {
            jsonWriter.WritePropertyName("error"u8);
            if (errorResponse.Error is not null)
            {
                WriteError(jsonWriter, errorResponse.Error);
            }
            else
            {
                jsonWriter.WriteNullValue();
            }
        }

        jsonWriter.WritePropertyName("id"u8);
        response.Id.WriteTo(jsonWriter);

        jsonWriter.WriteEndObject();
    }

    private static bool TryWriteSimpleResult(Utf8JsonWriter jsonWriter, object result)
    {
        switch (result)
        {
            case string value:
                jsonWriter.WriteStringValue(value);
                return true;
            case bool value:
                jsonWriter.WriteBooleanValue(value);
                return true;
            default:
                return false;
        }
    }

    private void WriteError(Utf8JsonWriter jsonWriter, Error error)
    {
        jsonWriter.WriteStartObject();
        jsonWriter.WriteNumber("code"u8, error.Code);
        jsonWriter.WriteString("message"u8, error.Message);
        jsonWriter.WritePropertyName("data"u8);

        object? data = error.Data;
        if (data is not null)
        {
            JsonSerializer.Serialize(jsonWriter, data, GetJsonTypeInfo(data.GetType()));
        }
        else
        {
            jsonWriter.WriteNullValue();
        }

        jsonWriter.WriteEndObject();
    }

    private JsonTypeInfo GetJsonTypeInfo(Type type) =>
        _jsonTypeInfoCache.GetOrAdd(type, static (type, options) => options.GetTypeInfo(type), jsonOptions);

    private static async ValueTask WriteStreamableResponseAsync(
        CountingWriter writer,
        JsonRpcResponse response,
        IStreamableResult streamable,
        CancellationToken cancellationToken)
    {
        writer.Write("{\"jsonrpc\":\"2.0\",\"result\":"u8);
        await streamable.WriteToAsync(writer, cancellationToken);
        writer.Write(",\"id\":"u8);
        WriteIdRaw(writer, response.Id);
        writer.Write("}"u8);
    }

    private static void WriteIdRaw(PipeWriter writer, JsonRpcId id)
    {
        if (!id.HasRawToken && id.TryGetInt64(out long longId))
        {
            Span<byte> buffer = writer.GetSpan(20);
            longId.TryFormat(buffer, out int written);
            writer.Advance(written);
            return;
        }

        if (!id.HasRawToken && id.TryGetDecimal(out decimal decimalId))
        {
            Span<byte> buffer = writer.GetSpan(32);
            decimalId.TryFormat(buffer, out int written);
            writer.Advance(written);
            return;
        }

        WriteOther(writer, id);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void WriteOther(PipeWriter writer, JsonRpcId id)
        {
            using Utf8JsonWriter jsonWriter = new(writer, new JsonWriterOptions { SkipValidation = true });
            id.WriteTo(jsonWriter);
        }
    }
}
