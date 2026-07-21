// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>
/// Streams a block's Geth-style tx traces as a JSON array, flushing after each entry
/// to avoid buffering the entire (potentially hundreds-of-MB) response in memory.
/// </summary>
[JsonConverter(typeof(GethLikeTxTraceStreamingResultConverter))]
public sealed class GethLikeTxTraceStreamingResult(IReadOnlyCollection<GethLikeTxTrace> traces)
    : IStreamableResult, IReadOnlyCollection<GethLikeTxTrace>, IDisposable
{
    public int Count => traces.Count;
    public IEnumerator<GethLikeTxTrace> GetEnumerator() => traces.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose()
    {
        traces.DisposeItems();
        (traces as IDisposable)?.Dispose();
    }

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        using Utf8JsonWriter jsonWriter = new(writer, new JsonWriterOptions { SkipValidation = true });

        jsonWriter.WriteStartArray();
        jsonWriter.Flush();

        try
        {
            foreach (GethLikeTxTrace trace in traces)
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName("result"u8);
                JsonSerializer.Serialize(jsonWriter, trace, EthereumJsonSerializer.JsonOptions);
                jsonWriter.WritePropertyName("txHash"u8);
                JsonSerializer.Serialize(jsonWriter, trace.TxHash, EthereumJsonSerializer.JsonOptions);
                jsonWriter.WriteEndObject();
                jsonWriter.Flush();

                FlushResult flushResult = await writer.FlushAsync(cancellationToken);
                if (flushResult.IsCompleted || flushResult.IsCanceled) return;
            }
        }
        finally
        {
            jsonWriter.WriteEndArray();
            jsonWriter.Flush();
        }
    }
}

public class GethLikeTxTraceStreamingResultConverter : JsonConverter<GethLikeTxTraceStreamingResult>
{
    public override GethLikeTxTraceStreamingResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, GethLikeTxTraceStreamingResult? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        JsonSerializer.Serialize(writer, new GethLikeTxTraceCollection(value), options);
    }
}
