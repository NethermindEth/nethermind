// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Collections;
using Nethermind.Facade.Filters;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// A logs result kept as the JSON of each block's logs, so that it is written without an object per log.
/// </summary>
internal sealed class RawLogsResponse : ResultWrapper<IEnumerable<FilterLog>>, IJsonRpcRawResponse
{
    private readonly ArrayPoolList<LogsJsonChunk> _chunks;

    public RawLogsResponse(ArrayPoolList<LogsJsonChunk> chunks)
    {
        _chunks = chunks;
        Data = [];
    }

    void IJsonRpcRawResponse.WriteRaw(IBufferWriter<byte> writer)
    {
        JsonRpcResponseWriter.WriteRawSuccessStart(writer);
        writer.Write("["u8);
        for (int i = 0; i < _chunks.Count; i++)
        {
            if (i != 0) writer.Write(","u8);
            writer.Write(_chunks[i].Json);
        }

        writer.Write("]"u8);
        JsonRpcResponseWriter.WriteRawSuccessEnd(writer, in IdRef);
    }

    /// <summary>Writes through <paramref name="writer"/>, which applies its own formatting, such as indentation.</summary>
    internal override void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        ArrayBufferWriter<byte> raw = new();
        ((IJsonRpcRawResponse)this).WriteRaw(raw);
        using JsonDocument document = JsonDocument.Parse(raw.WrittenMemory);
        document.WriteTo(writer);
    }

    protected override void DisposePayloads() => DisposeChunks(_chunks);

    internal static void DisposeChunks(ArrayPoolList<LogsJsonChunk> chunks)
    {
        foreach (LogsJsonChunk chunk in chunks)
        {
            chunk.Dispose();
        }

        chunks.Dispose();
    }
}
