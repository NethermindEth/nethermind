// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    private static ReadOnlySpan<byte> SuccessEnvelopeStart => "{\"jsonrpc\":\"2.0\",\"result\":"u8;
    private static ReadOnlySpan<byte> IdSeparator => ",\"id\":"u8;
    private static ReadOnlySpan<byte> EnvelopeEnd => "}"u8;
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
    {
        if (response.TryGetStreamableResult(out IStreamableResult? streamable))
        {
            return WriteStreamableAsync(writer, response, streamable, cancellationToken);
        }

        Write(writer, response, options);
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns whether <paramref name="response"/> should map to HTTP 503 on HTTP transports.</summary>
    public static bool IsResourceUnavailableError(JsonRpcResponse? response) =>
        response?.IsResourceUnavailableError == true;

    private static async ValueTask WriteStreamableAsync(
        PipeWriter writer,
        JsonRpcResponse response,
        IStreamableResult streamable,
        CancellationToken cancellationToken)
    {
        writer.Write(SuccessEnvelopeStart);
        await streamable.WriteToAsync(writer, cancellationToken);
        writer.Write(IdSeparator);
        WriteIdRaw(writer, response.Id);
        writer.Write(EnvelopeEnd);
    }

    internal static void WriteEnvelopeStart(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc"u8, "2.0"u8);
    }

    internal static void WriteEnvelopeEnd(Utf8JsonWriter writer, JsonRpcId id)
    {
        writer.WritePropertyName("id"u8);
        id.WriteTo(writer);
        writer.WriteEndObject();
    }

    internal static void WriteRawSuccess(IBufferWriter<byte> writer, ReadOnlySpan<byte> rawResult, JsonRpcId id)
    {
        writer.Write(SuccessEnvelopeStart);
        writer.Write(rawResult);
        writer.Write(IdSeparator);
        WriteIdRaw(writer, id);
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

    private static void WriteIdRaw(IBufferWriter<byte> writer, JsonRpcId id)
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
        static void WriteOther(IBufferWriter<byte> writer, JsonRpcId id)
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
