// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// Streams an array of <typeparamref name="T"/> items directly to the response pipe,
/// flushing after each item to avoid buffering the entire result in memory.
/// </summary>
/// <remarks>
/// When the HTTP pipeline detects <see cref="IStreamableResult"/> on the response, it calls
/// <see cref="WriteToAsync"/> instead of serialising through the normal JSON path, allowing
/// traces to be emitted incrementally as they are produced.
///
/// The <see cref="IEnumerable{T}"/> implementation provides a synchronous fallback used by
/// buffered-response paths (e.g. batch requests) and unit tests.
/// </remarks>
[JsonConverter(typeof(ParityTxTraceStreamingResultConverterFactory))]
public sealed class ParityTxTraceStreamingResult<T>(IAsyncEnumerable<T> source)
    : IStreamableResult, IEnumerable<T>
{
    public async ValueTask WriteToAsync(PipeWriter pipeWriter, CancellationToken cancellationToken)
    {
        using Utf8JsonWriter jsonWriter = new(pipeWriter, new JsonWriterOptions { SkipValidation = true });

        jsonWriter.WriteStartArray();
        jsonWriter.Flush();
        await pipeWriter.FlushAsync(cancellationToken);

        await foreach (T item in source.WithCancellation(cancellationToken))
        {
            JsonSerializer.Serialize(jsonWriter, item, EthereumJsonSerializer.JsonOptions);
            jsonWriter.Flush();

            FlushResult flushResult = await pipeWriter.FlushAsync(cancellationToken);
            if (flushResult.IsCompleted || flushResult.IsCanceled) return;
        }

        jsonWriter.WriteEndArray();
        jsonWriter.Flush();
    }

    public IEnumerator<T> GetEnumerator() =>
        source.ToBlockingEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class ParityTxTraceStreamingResultConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(ParityTxTraceStreamingResult<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type itemType = typeToConvert.GetGenericArguments()[0];
        Type converterType = typeof(ParityTxTraceStreamingResultConverter<>).MakeGenericType(itemType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

public sealed class ParityTxTraceStreamingResultConverter<T> : JsonConverter<ParityTxTraceStreamingResult<T>>
{
    public override ParityTxTraceStreamingResult<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, ParityTxTraceStreamingResult<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (T item in value)
        {
            JsonSerializer.Serialize(writer, item, options);
        }
        writer.WriteEndArray();
    }
}
