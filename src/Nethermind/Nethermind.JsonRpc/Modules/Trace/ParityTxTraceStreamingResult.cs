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
/// <see cref="StreamingParityLikeBlockTracer"/>. <see cref="WriteToAsync"/> opens the
/// outer JSON array, runs the delegate (which emits items per-tx directly), and closes
/// the array on return.
/// </summary>
/// <remarks>
/// In-process iteration via <see cref="IEnumerable{T}"/> uses the buffered fallback
/// (the items only exist as JSON bytes in the streaming writer); HTTP clients go through
/// <see cref="IStreamableResult.WriteToAsync"/>.
/// </remarks>
[JsonConverter(typeof(ParityTxTraceStreamingResultConverterFactory))]
public sealed class ParityTxTraceStreamingResult<T> : IStreamableResult, IEnumerable<T>, IDisposable
{
    private readonly Action<Utf8JsonWriter, PipeWriter?, CancellationToken> _runExecution;
    private readonly Func<IEnumerable<T>>? _materializeForInProcess;
    private readonly CancellationTokenSource _timeoutCts;

    /// <param name="materializeForInProcess">
    /// Buffered fallback for in-process enumeration; HTTP clients never hit this. If
    /// <see langword="null"/>, in-process enumeration yields no items.
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

    // In-process fallback used by the JSON converter; no pipe-level flushing.
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
