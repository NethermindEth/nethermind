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

[JsonConverter(typeof(ParityTxTraceStreamingResultConverterFactory))]
public sealed class ParityTxTraceStreamingResult<T> : JsonStreamingResultBase, IEnumerable<T>
{
    private readonly Action<Utf8JsonWriter, PipeWriter?, CancellationToken> _runExecution;

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
