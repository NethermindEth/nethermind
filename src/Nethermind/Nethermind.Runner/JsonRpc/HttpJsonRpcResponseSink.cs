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
    private static readonly ConcurrentDictionary<Type, JsonTypeInfo> _jsonTypeInfoCache = new();

    private CountingWriter? _writer;
    private Stream? _bufferedStream;
    private bool _isFirstBatchItem = true;
    private bool _completed;

    public long BytesWritten => _writer?.WrittenCount ?? 0;
    public bool StopRequested { get; private set; }

    public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        long responseWriteStartTimestamp = Stopwatch.GetTimestamp();
        ValueTask startTask = EnsureStartedAsync(isCollection: false, response, cancellationToken);
        if (!startTask.IsCompletedSuccessfully)
        {
            return WriteSingleAfterStartAsync(startTask, response, report, responseWriteStartTimestamp, cancellationToken);
        }

        startTask.GetAwaiter().GetResult();
        return WriteSingleStartedAsync(response, report, responseWriteStartTimestamp, cancellationToken);
    }

    private ValueTask WriteSingleStartedAsync(JsonRpcResponse response, RpcReport report, long responseWriteStartTimestamp, CancellationToken cancellationToken)
    {
        ValueTask writeTask = WriteResponseAsync(_writer!, response, cancellationToken);
        if (!writeTask.IsCompletedSuccessfully)
        {
            return WriteSingleAfterWriteAsync(writeTask, report, responseWriteStartTimestamp);
        }

        writeTask.GetAwaiter().GetResult();
        ReportSingle(report, responseWriteStartTimestamp);
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteSingleAfterStartAsync(
        ValueTask startTask,
        JsonRpcResponse response,
        RpcReport report,
        long responseWriteStartTimestamp,
        CancellationToken cancellationToken)
    {
        await startTask;
        await WriteResponseAsync(_writer!, response, cancellationToken);
        ReportSingle(report, responseWriteStartTimestamp);
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
            BoundaryTimings = report.BoundaryTimings.WithResponseWrite((long)Stopwatch.GetElapsedTime(responseWriteStartTimestamp).TotalMicroseconds)
        };

        long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(requestStartTimestamp).TotalMicroseconds;
        jsonRpcLocalStats.ReportCall(report, handlingTimeMicroseconds, BytesWritten);
    }

    public ValueTask BeginBatchAsync(CancellationToken cancellationToken)
    {
        ValueTask startTask = EnsureStartedAsync(isCollection: true, response: null, cancellationToken);
        if (!startTask.IsCompletedSuccessfully)
        {
            return BeginBatchAfterStartAsync(startTask);
        }

        startTask.GetAwaiter().GetResult();
        _writer!.Write(JsonOpeningBracket);
        return ValueTask.CompletedTask;
    }

    private async ValueTask BeginBatchAfterStartAsync(ValueTask startTask)
    {
        await startTask;
        _writer!.Write(JsonOpeningBracket);
    }

    public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        long responseWriteStartTimestamp = Stopwatch.GetTimestamp();
        if (!_isFirstBatchItem)
        {
            _writer!.Write(JsonComma);
        }

        _isFirstBatchItem = false;

        ValueTask writeTask = WriteResponseAsync(_writer!, response, cancellationToken);
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
            BoundaryTimings = report.BoundaryTimings.WithResponseWrite((long)Stopwatch.GetElapsedTime(responseWriteStartTimestamp).TotalMicroseconds)
        };

        jsonRpcLocalStats.ReportCall(report);

        if (!jsonRpcUrl.IsAuthenticated && BytesWritten > jsonRpcConfig.MaxBatchResponseBodySize)
        {
            if (logger.IsWarn) logger.Warn($"The max batch response body size exceeded. The current response size {BytesWritten}, and the config setting is JsonRpc.{nameof(jsonRpcConfig.MaxBatchResponseBodySize)} = {jsonRpcConfig.MaxBatchResponseBodySize}");
            StopRequested = true;
        }
    }

    public ValueTask EndBatchAsync(CancellationToken cancellationToken)
    {
        _writer!.Write(JsonClosingBracket);

        long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(requestStartTimestamp).TotalMicroseconds;
        jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, BytesWritten);

        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed || _writer is null)
        {
            return;
        }

        _completed = true;
        await _writer.CompleteAsync();

        if (_bufferedStream is not null)
        {
            context.Response.ContentLength = BytesWritten;
            _bufferedStream.Seek(0, SeekOrigin.Begin);
            await _bufferedStream.CopyToAsync(context.Response.Body, cancellationToken);
            await _bufferedStream.DisposeAsync();
        }

        Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, BytesWritten);
        await context.Response.CompleteAsync();
    }

    private ValueTask EnsureStartedAsync(bool isCollection, JsonRpcResponse? response, CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            return ValueTask.CompletedTask;
        }

        bool bufferResponse = jsonRpcConfig.BufferResponses && !(jsonRpcUrl.IsAuthenticated && !isCollection);
        _bufferedStream = bufferResponse ? RecyclableStream.GetStream("http") : null;
        _writer = _bufferedStream is not null ? new CountingStreamPipeWriter(_bufferedStream) : new CountingPipeWriter(context.Response.BodyWriter);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = isCollection
            ? StatusCodes.Status200OK
            : GetStatusCode(response);

        if (_bufferedStream is null)
        {
            return new ValueTask(context.Response.StartAsync(cancellationToken));
        }

        return ValueTask.CompletedTask;
    }

    private static int GetStatusCode(JsonRpcResponse? response) =>
        IsResourceUnavailableError(response)
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

    private static bool IsResourceUnavailableError(JsonRpcResponse? response) => response is JsonRpcErrorResponse { Error.Code: ErrorCodes.ModuleTimeout }
        or JsonRpcErrorResponse { Error.Code: ErrorCodes.LimitExceeded };

    private ValueTask WriteResponseAsync(CountingWriter writer, JsonRpcResponse response, CancellationToken cancellationToken)
    {
        if (response is JsonRpcSuccessResponse { Result: IStreamableResult streamable })
        {
            return WriteStreamableResponseAsync(writer, response, streamable, cancellationToken);
        }

        WriteJsonRpcResponse(writer, response);
        return ValueTask.CompletedTask;
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
                JsonSerializer.Serialize(jsonWriter, result, GetJsonTypeInfo(result.GetType()));
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
