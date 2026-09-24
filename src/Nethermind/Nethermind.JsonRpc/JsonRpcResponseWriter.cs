// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;
using Nethermind.Core.Resettables;
using Nethermind.Serialization.Json;

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
    public static async ValueTask WriteAsync(PipeWriter writer, JsonRpcResponse response, JsonSerializerOptions options, bool isBatch, CancellationToken cancellationToken)
        => await WriteWithOutcomeAsync(writer, response, options, isBatch, cancellationToken);

    internal static async ValueTask<JsonRpcResponseWriteOutcome> WriteWithOutcomeAsync(
        PipeWriter writer, JsonRpcResponse response, JsonSerializerOptions options, bool isBatch,
        CancellationToken cancellationToken, bool bufferResponse = false)
    {
        if (!response.TryGetStreamableResult(out IStreamableResult? streamable))
        {
            Write(writer, response, options);
            return JsonRpcResponseWriteOutcome.Of(response);
        }

        bool success = false;
        try
        {
            JsonRpcResponseWriteOutcome outcome = await WriteStreamableWithErrorHandlingAsync(
                writer, response, streamable, options, isBatch, bufferResponse ? long.MaxValue : StagingPipeWriter.DefaultLimit, cancellationToken);
            success = outcome.Success;
            return outcome;
        }
        finally
        {
            response.StreamCompleted?.Invoke(success);
            response.StreamCompleted = null;
        }
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

    private static async ValueTask<JsonRpcResponseWriteOutcome> WriteStreamableWithErrorHandlingAsync(
        PipeWriter writer,
        JsonRpcResponse response,
        IStreamableResult streamable,
        JsonSerializerOptions options,
        bool isBatch,
        long stagingLimit,
        CancellationToken cancellationToken)
    {
        using StagingPipeWriter staged = new(writer, stagingLimit);
        try
        {
            await WriteStreamableAsync(staged, response, streamable, isBatch, cancellationToken);
        }
        // Nothing reaches the transport before commitment, so an uncommitted failure is never a transport failure.
        catch (Exception ex) when (!staged.IsCommitted && !cancellationToken.IsCancellationRequested && response.StreamExceptionHandler is not null)
        {
            using JsonRpcErrorResponse error = response.StreamExceptionHandler(ex);
            Write(writer, error, options);
            return JsonRpcResponseWriteOutcome.Of(error);
        }
        staged.Commit();
        return JsonRpcResponseWriteOutcome.Of(response);
    }

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

    /// <summary>
    /// Resolves the contract to serialize <paramref name="value"/> with, or null when the declared
    /// <typeparamref name="TValue"/> contract already describes it.
    /// </summary>
    /// <remarks>
    /// Serializing a derived payload at its declared type silently omits the properties the derived type
    /// adds — the engine-specific seal fields consensus plugins put on <c>BlockForRpc</c>, for instance —
    /// because the payload types carry no <c>JsonDerivedType</c> attributes.
    /// </remarks>
    internal static JsonTypeInfo? GetRuntimePayloadTypeInfo<TValue>(JsonSerializerOptions options, TValue value)
    {
        if (!RpcPayloadTypeShape<TValue>.CanHaveDerivedRuntimeType)
        {
            return null;
        }

        Type runtimeType = value!.GetType();
        return runtimeType == typeof(TValue) ? null : RpcPayloadTypeInfo.Get(options, runtimeType);
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

internal readonly record struct JsonRpcResponseWriteOutcome(bool Success, bool IsResourceUnavailable)
{
    internal static JsonRpcResponseWriteOutcome Of(JsonRpcResponse response) =>
        new(!response.TryGetError(out Error? error) || error is null, response.IsResourceUnavailableError);

    internal RpcReport ApplyTo(RpcReport report) => report with { Success = Success };
}

/// <summary>Stages the beginning of a response until deferred execution succeeds or exceeds the staging limit.</summary>
/// <remarks>
/// Flushes below the limit remain local, so their results never report a completed or cancelled reader. Once
/// committed, bytes may have reached the transport and the caller must abort on failure. Transports that buffer
/// the whole response stage it without a limit, so a failure can always replace the current response.
/// </remarks>
internal sealed class StagingPipeWriter : CountingWriter, IDisposable
{
    internal const int DefaultLimit = 16 * 1024;
    private readonly PipeWriter _writer;
    private readonly long _limit;
    private RecyclableMemoryStream? _buffer = RecyclableStream.GetStream("json-rpc-response");

    internal StagingPipeWriter(PipeWriter writer, long limit)
    {
        _writer = writer;
        _limit = limit;
        WrittenCount = (writer as CountingWriter)?.WrittenCount ?? 0;
    }

    internal bool IsCommitted => _buffer is null;
    public override bool CanGetUnflushedBytes => _writer.CanGetUnflushedBytes;
    public override long UnflushedBytes => _writer.UnflushedBytes + (_buffer?.Length ?? 0);

    internal void Commit()
    {
        if (_buffer is not { } buffer) return;
        _buffer = null;
        using (buffer)
        {
            foreach (ReadOnlyMemory<byte> segment in buffer.GetReadOnlySequence())
            {
                _writer.Write(segment.Span);
            }
        }
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_buffer is { } buffer)
        {
            long remaining = _limit - buffer.Length;
            if (Math.Max(sizeHint, 1) <= remaining)
            {
                Memory<byte> memory = buffer.GetMemory(sizeHint);
                return memory[..(int)Math.Min(memory.Length, remaining)];
            }
            Commit();
        }
        return _writer.GetMemory(sizeHint);
    }

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public override void Advance(int bytes)
    {
        if (_buffer is { } buffer) buffer.Advance(bytes);
        else _writer.Advance(bytes);
        WrittenCount += bytes;
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return IsCommitted ? _writer.FlushAsync(cancellationToken) : new(new FlushResult(false, false));
    }

    public override void CancelPendingFlush() => _writer.CancelPendingFlush();

    public override void Complete(Exception? exception = null)
    {
        Commit();
        _writer.Complete(exception);
    }
}
