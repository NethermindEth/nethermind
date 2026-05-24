// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// Streaming result wrapping a deferred execution delegate that drives a
/// <see cref="StreamingParityLikeBlockTracer"/> (or a multi-block iteration thereof). The
/// outer JSON array is opened by <see cref="WriteToAsync"/>, the delegate executes the
/// EVM and emits items per-tx directly to the response, and the array is closed when
/// execution returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-process consumers:</b> the <see cref="IEnumerable{T}"/> implementation is
/// intentionally empty — the items don't exist as objects, they only exist as JSON
/// bytes in the writer. HTTP/JSON-RPC clients work through the registered
/// <see cref="IStreamableResult"/> path. In-process callers that need to enumerate the
/// items must use the buffered, non-streaming path instead.
/// </para>
/// </remarks>
[JsonConverter(typeof(ParityTxTraceStreamingResultConverterFactory))]
public sealed class ParityTxTraceStreamingResult<T> : IStreamableResult, IEnumerable<T>, IDisposable
{
    private readonly Action<Utf8JsonWriter, PipeWriter?, CancellationToken> _runExecution;
    private readonly Func<IEnumerable<T>>? _materializeForInProcess;
    private readonly CancellationTokenSource _timeoutCts;

    /// <param name="materializeForInProcess">
    /// Buffered fallback used when this result is iterated in-process via <see cref="IEnumerable{T}"/>
    /// (e.g. test code calling <c>resultWrapper.Data.Count()</c>). Production HTTP/JSON-RPC clients
    /// go through <see cref="IStreamableResult.WriteToAsync"/> and never hit this path; the
    /// fallback re-runs the trace through the buffered tracer to satisfy in-process consumers.
    /// If <see langword="null"/>, in-process enumeration yields no items.
    /// </param>
    public ParityTxTraceStreamingResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runExecution,
        CancellationTokenSource timeoutCts,
        Func<IEnumerable<T>>? materializeForInProcess = null)
    {
        ArgumentNullException.ThrowIfNull(runExecution);
        ArgumentNullException.ThrowIfNull(timeoutCts);

        _runExecution = runExecution;
        _materializeForInProcess = materializeForInProcess;
        _timeoutCts = timeoutCts;
    }

    public IEnumerator<T> GetEnumerator() =>
        _materializeForInProcess is null
            ? Enumerable.Empty<T>().GetEnumerator()
            : _materializeForInProcess().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _timeoutCts.Dispose();

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(_timeoutCts.Token, cancellationToken);
        CancellationToken token = linkedCts.Token;

        using Utf8JsonWriter jsonWriter = new(writer, new JsonWriterOptions { SkipValidation = true });

        jsonWriter.WriteStartArray();
        jsonWriter.Flush();

        try
        {
            _runExecution(jsonWriter, writer, token);
        }
        finally
        {
            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
            await writer.FlushAsync(token);
        }
    }

    /// <summary>
    /// In-process fallback used by the JSON converter. Drives execution into the supplied
    /// writer with no pipe-level flushing; the buffer grows to hold the full response.
    /// </summary>
    internal void WriteAsJson(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        try
        {
            _runExecution(writer, null, _timeoutCts.Token);
        }
        finally
        {
            writer.WriteEndArray();
        }
    }
}

internal sealed class ParityTxTraceStreamingResultConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ParityTxTraceStreamingResult<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type element = typeToConvert.GetGenericArguments()[0];
        Type converterType = typeof(ParityTxTraceStreamingResultConverter<>).MakeGenericType(element);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class ParityTxTraceStreamingResultConverter<T> : JsonConverter<ParityTxTraceStreamingResult<T>>
{
    public override ParityTxTraceStreamingResult<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, ParityTxTraceStreamingResult<T>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        value.WriteAsJson(writer);
    }
}
