 // SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>
/// Streams a block's Geth-style tx traces as a JSON array, flushing after each entry
/// to avoid buffering the entire (potentially hundreds-of-MB) response in memory.
/// </summary>
public sealed class GethLikeTxTraceStreamingResult(IReadOnlyCollection<GethLikeTxTrace> traces)
    : IStreamableResult, IReadOnlyCollection<GethLikeTxTrace>, IDisposable
{
    public int Count => traces.Count;
    public IEnumerator<GethLikeTxTrace> GetEnumerator() => traces.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => (traces as IDisposable)?.Dispose();

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        writer.Write("["u8);

        using Utf8JsonWriter jsonWriter = new(writer, new JsonWriterOptions { SkipValidation = true });
        bool first = true;

        foreach (GethLikeTxTrace trace in traces)
        {
            // Write separator before each entry except the first.
            // Safe to write directly: jsonWriter was flushed at end of previous iteration.
            if (!first) writer.Write(","u8);
            first = false;

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

        writer.Write("]"u8);
    }
}
