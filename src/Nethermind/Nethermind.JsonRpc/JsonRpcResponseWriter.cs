// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

/// <summary>Writes server-side JSON-RPC response objects to transport-owned buffers.</summary>
public static class JsonRpcResponseWriter
{
    private static readonly byte[] BatchStart = [(byte)'['];
    private static readonly byte[] BatchSeparator = [(byte)','];
    private static readonly byte[] BatchEnd = [(byte)']'];
    private static ReadOnlySpan<byte> SuccessEnvelopeStart => "{\"jsonrpc\":\"2.0\",\"result\":"u8;
    private static ReadOnlySpan<byte> StreamStatusSeparator => ",\"_streamStatus\":\""u8;
    private static ReadOnlySpan<byte> IdSeparator => ",\"id\":"u8;
    private static ReadOnlySpan<byte> EnvelopeEnd => "}"u8;
    private static ReadOnlySpan<byte> Quote => "\""u8;
    private static readonly JsonWriterOptions _streamableIdWriterOptions = new()
    {
        SkipValidation = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Writes <paramref name="response"/> as a JSON-RPC response envelope.</summary>
    public static void Write(IBufferWriter<byte> writer, JsonRpcResponse response, JsonSerializerOptions options)
    {
        if (!options.WriteIndented && response is IJsonRpcRawResponse rawResponse)
        {
            rawResponse.WriteRaw(writer);
            return;
        }

        using Utf8JsonWriter jsonWriter = new(writer, CreateWriterOptions(options));
        response.WriteTo(jsonWriter, options);
    }

    /// <summary>Writes <paramref name="response"/>, using the streamable result path when required.</summary>
    public static ValueTask WriteAsync(PipeWriter writer, JsonRpcResponse response, JsonSerializerOptions options, CancellationToken cancellationToken)
        => WriteAsync(writer, response, options, isBatch: false, cancellationToken);

    /// <summary>Writes <paramref name="response"/>, using the streamable result path when required.</summary>
    public static ValueTask WriteAsync(PipeWriter writer, JsonRpcResponse response, JsonSerializerOptions options, bool isBatch, CancellationToken cancellationToken)
    {
        if (response.TryGetStreamableResult(out IStreamableResult? streamable))
        {
            return WriteStreamableAsync(writer, response, streamable, isBatch, cancellationToken);
        }

        Write(writer, response, options);
        return ValueTask.CompletedTask;
    }

    /// <summary>Writes the opening token for a JSON-RPC batch response.</summary>
    public static void WriteBatchStart(IBufferWriter<byte> writer) => writer.Write(BatchStart);

    /// <summary>Writes the separator token between JSON-RPC batch response items.</summary>
    public static void WriteBatchSeparator(IBufferWriter<byte> writer) => writer.Write(BatchSeparator);

    /// <summary>Writes the closing token for a JSON-RPC batch response.</summary>
    public static void WriteBatchEnd(IBufferWriter<byte> writer) => writer.Write(BatchEnd);

    /// <summary>Writes the opening token for a JSON-RPC batch response.</summary>
    public static ValueTask WriteBatchStartAsync(Stream stream, CancellationToken cancellationToken) =>
        stream.WriteAsync(BatchStart, cancellationToken);

    /// <summary>Writes the separator token between JSON-RPC batch response items.</summary>
    public static ValueTask WriteBatchSeparatorAsync(Stream stream, CancellationToken cancellationToken) =>
        stream.WriteAsync(BatchSeparator, cancellationToken);

    /// <summary>Writes the closing token for a JSON-RPC batch response.</summary>
    public static ValueTask WriteBatchEndAsync(Stream stream, CancellationToken cancellationToken) =>
        stream.WriteAsync(BatchEnd, cancellationToken);

    /// <summary>Returns whether <paramref name="response"/> should map to HTTP 503 on HTTP transports.</summary>
    public static bool IsResourceUnavailableError(JsonRpcResponse? response) =>
        response?.IsResourceUnavailableError == true;

    private static async ValueTask WriteStreamableAsync(
        PipeWriter writer,
        JsonRpcResponse response,
        IStreamableResult streamable,
        bool isBatch,
        CancellationToken cancellationToken)
    {
        writer.Write(SuccessEnvelopeStart);
        StreamableResultStatus? status = null;
        if (streamable is IBatchAwareStreamableResultWithStatus batchAwareStatusStreamable)
        {
            status = await batchAwareStatusStreamable.WriteToWithStatusAsync(writer, isBatch, cancellationToken);
        }
        else if (streamable is IStreamableResultWithStatus statusStreamable)
        {
            status = await statusStreamable.WriteToWithStatusAsync(writer, cancellationToken);
        }
        else if (streamable is IBatchAwareStreamableResult batchAwareStreamable)
        {
            await batchAwareStreamable.WriteToAsync(writer, isBatch, cancellationToken);
        }
        else
        {
            await streamable.WriteToAsync(writer, cancellationToken);
        }
        if (status is not null)
        {
            writer.Write(StreamStatusSeparator);
            writer.Write(GetStreamStatusBytes(status.GetValueOrDefault()));
            writer.Write(Quote);
        }
        writer.Write(IdSeparator);
        WriteIdRaw(writer, in response.IdRef);
        writer.Write(EnvelopeEnd);
    }

    private static ReadOnlySpan<byte> GetStreamStatusBytes(StreamableResultStatus status) =>
        status switch
        {
            StreamableResultStatus.Complete => "complete"u8,
            StreamableResultStatus.Timeout => "timeout"u8,
            StreamableResultStatus.Truncated => "truncated"u8,
            StreamableResultStatus.Cancelled => "cancelled"u8,
            StreamableResultStatus.Failed => "failed"u8,
            _ => "failed"u8
        };

    internal static void WriteEnvelopeStart(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc"u8, "2.0"u8);
    }

    internal static void WriteEnvelopeEnd(Utf8JsonWriter writer, in JsonRpcId id)
    {
        writer.WritePropertyName("id"u8);
        id.WriteTo(writer);
        writer.WriteEndObject();
    }

    internal static void WriteRawSuccess(IBufferWriter<byte> writer, ReadOnlySpan<byte> rawResult, in JsonRpcId id)
    {
        writer.Write(SuccessEnvelopeStart);
        writer.Write(rawResult);
        writer.Write(IdSeparator);
        WriteIdRaw(writer, in id);
        writer.Write(EnvelopeEnd);
    }

    internal static bool TryWriteSimpleValue<T>(Utf8JsonWriter writer, T value)
    {
        if (typeof(T) == typeof(string))
        {
            writer.WriteStringValue(Unsafe.As<T, string>(ref value));
            return true;
        }

        if (typeof(T) == typeof(bool))
        {
            writer.WriteBooleanValue(Unsafe.As<T, bool>(ref value));
            return true;
        }

        if (typeof(T) == typeof(int))
        {
            writer.WriteNumberValue(Unsafe.As<T, int>(ref value));
            return true;
        }

        return false;
    }

    internal static bool TryWriteSimpleObject(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case string stringValue:
                writer.WriteStringValue(stringValue);
                return true;
            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                return true;
            case int intValue:
                writer.WriteNumberValue(intValue);
                return true;
            default:
                return false;
        }
    }

    internal static void WriteErrorObject(Utf8JsonWriter writer, Error error, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("code"u8, error.Code);
        writer.WriteString("message"u8, error.Message);

        object? data = error.Data;
        if (data is not null)
        {
            writer.WritePropertyName("data"u8);
            JsonSerializer.Serialize(writer, data, RpcPayloadTypeInfo.Get(options, data.GetType()));
        }

        writer.WriteEndObject();
    }

    private static void WriteIdRaw(IBufferWriter<byte> writer, in JsonRpcId id)
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

        WriteOther(writer, in id);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void WriteOther(IBufferWriter<byte> writer, in JsonRpcId id)
        {
            using Utf8JsonWriter jsonWriter = new(writer, _streamableIdWriterOptions);
            id.WriteTo(jsonWriter);
        }
    }

    private static JsonWriterOptions CreateWriterOptions(JsonSerializerOptions options) => new()
    {
        SkipValidation = true,
        Indented = options.WriteIndented,
        Encoder = options.Encoder,
        MaxDepth = options.MaxDepth
    };
}

internal interface IJsonRpcRawResponse
{
    void WriteRaw(IBufferWriter<byte> writer);
}
