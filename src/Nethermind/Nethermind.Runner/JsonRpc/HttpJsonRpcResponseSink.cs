// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

    private CountingWriter? _writer;
    private Stream? _bufferedStream;
    private bool _isFirstBatchItem = true;
    private bool _completed;

    public long BytesWritten => _writer?.WrittenCount ?? 0;
    public bool StopRequested { get; private set; }

    public async ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(isCollection: false, response, cancellationToken);
        await WriteResponseAsync(_writer!, response, cancellationToken);

        long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(requestStartTimestamp).TotalMicroseconds;
        _ = jsonRpcLocalStats.ReportCall(report, handlingTimeMicroseconds, BytesWritten);
    }

    public async ValueTask BeginBatchAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(isCollection: true, response: null, cancellationToken);
        _writer!.Write(JsonOpeningBracket);
    }

    public async ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
    {
        if (!_isFirstBatchItem)
        {
            _writer!.Write(JsonComma);
        }

        _isFirstBatchItem = false;

        await WriteResponseAsync(_writer!, response, cancellationToken);
        _ = jsonRpcLocalStats.ReportCall(report);

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
        _ = jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, BytesWritten);

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

    private async ValueTask EnsureStartedAsync(bool isCollection, JsonRpcResponse? response, CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            return;
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
            await context.Response.StartAsync(cancellationToken);
        }
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
                JsonSerializer.Serialize(jsonWriter, result, result.GetType(), jsonOptions);
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
                JsonSerializer.Serialize(jsonWriter, errorResponse.Error, jsonOptions);
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
        if (id.TryGetInt64(out long longId))
        {
            Span<byte> buffer = writer.GetSpan(20);
            longId.TryFormat(buffer, out int written);
            writer.Advance(written);
            return;
        }

        if (id.TryGetDecimal(out decimal decimalId))
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
