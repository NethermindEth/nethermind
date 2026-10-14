// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Collections;
using Nethermind.Facade.Filters;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// A logs result kept as the JSON of each block's logs, so that it is written without an object per log.
/// </summary>
/// <remarks>
/// <see cref="ResultWrapper{T}.Data"/> runs the same query again as <see cref="FilterLog"/> objects when an
/// in-process caller enumerates it; the transports write the JSON and never enumerate it.
/// </remarks>
internal sealed class RawLogsResponse : ResultWrapper<IEnumerable<FilterLog>>, IJsonRpcRawResponse
{
    private readonly ArrayPoolList<LogsJsonChunk> _chunks;

    public RawLogsResponse(ArrayPoolList<LogsJsonChunk> chunks, Func<IEnumerable<FilterLog>> findLogs)
    {
        _chunks = chunks;
        Data = new DeferredLogs(findLogs);
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

    private sealed class DeferredLogs(Func<IEnumerable<FilterLog>> findLogs) : IEnumerable<FilterLog>
    {
        public IEnumerator<FilterLog> GetEnumerator() => findLogs().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static void DisposeChunks(ArrayPoolList<LogsJsonChunk> chunks)
    {
        foreach (LogsJsonChunk chunk in chunks)
        {
            chunk.Dispose();
        }

        chunks.Dispose();
    }
}
