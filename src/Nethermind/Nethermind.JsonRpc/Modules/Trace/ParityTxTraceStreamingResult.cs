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
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// Streaming result wrapping a deferred execution delegate that drives a
/// <see cref="StreamingParityLikeBlockTracer"/>. The outer JSON array is opened by
/// <see cref="StreamingResultBase.WriteToAsync"/>, the delegate runs the trace and emits
/// items per-tx directly, and the array is closed on return.
/// </summary>
/// <remarks>
/// In-process iteration via <see cref="IEnumerable{T}"/> uses the buffered fallback
/// (the items only exist as JSON bytes in the streaming writer); HTTP clients go through
/// <see cref="IStreamableResult.WriteToAsync"/>.
/// </remarks>
[JsonConverter(typeof(ParityTxTraceStreamingResultConverterFactory))]
public sealed class ParityTxTraceStreamingResult<T> : StreamingResultBase, IEnumerable<T>
{
    private readonly Action<Utf8JsonWriter, PipeWriter?, CancellationToken> _runExecution;

    /// <summary>
    /// Buffered fallback for in-process enumeration; HTTP clients never hit this. If
    /// <see langword="null"/>, in-process enumeration yields no items.
    /// </summary>
    public Func<IEnumerable<T>>? MaterializeForInProcess { get; init; }

    public ParityTxTraceStreamingResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runExecution,
        CancellationTokenSource timeoutCts,
        ILogger logger)
        : base(timeoutCts, logger)
    {
        ArgumentNullException.ThrowIfNull(runExecution);

        _runExecution = runExecution;
    }

    /// <summary>
    /// Re-enumeration is safe and produces a fresh trace on every call. Unlike the
    /// single-result variant <see cref="ParityTxTraceFromReplayStreamingResult"/>, which
    /// pins a worldstate <c>Scope&lt;ITracer&gt;</c> across the deferred trace and therefore
    /// gates streaming-vs-in-process consumption behind a single <c>_consumed</c> flag,
    /// the multi-tx endpoints' streaming delegates open their own independent execution
    /// scope inside <c>TraceBlockWithCancellation</c> / <c>ExecuteBlockWithCancellation</c>
    /// each time. No shared state across calls, no idempotency guard needed.
    /// </summary>
    public IEnumerator<T> GetEnumerator() =>
        MaterializeForInProcess is null
            ? Enumerable.Empty<T>().GetEnumerator()
            : MaterializeForInProcess().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
    {
        writer.WriteStartArray();
        try
        {
            _runExecution(writer, pipeWriter, cancellationToken);
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
