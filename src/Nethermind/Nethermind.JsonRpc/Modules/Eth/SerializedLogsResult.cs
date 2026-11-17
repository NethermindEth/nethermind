// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Facade.Filters;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>A buffered logs response, kept as the JSON of each log until the response is written.</summary>
/// <remarks>Enumerating it re-runs the query the logs came from, so only callers outside the response writer should.</remarks>
[JsonConverter(typeof(SerializedLogsResultConverter))]
internal sealed class SerializedLogsResult(IEnumerable<FilterLog> logs) : IEnumerable<FilterLog>, IDisposable
{
    private readonly ArrayPoolList<SerializedLogs> _blocks = [with(16)];

    public int Count { get; private set; }

    internal ReadOnlySpan<SerializedLogs> Blocks => _blocks.AsSpan();

    public void Add(SerializedLogs blockLogs)
    {
        _blocks.Add(blockLogs);
        Count += blockLogs.Count;
    }

    public IEnumerator<FilterLog> GetEnumerator() => logs.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        foreach (SerializedLogs blockLogs in _blocks.AsSpan())
        {
            blockLogs.Dispose();
        }

        _blocks.Dispose();
    }
}

internal sealed class SerializedLogsResultConverter : JsonConverter<SerializedLogsResult>
{
    public override SerializedLogsResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(SerializedLogsResultConverter)} is serialize-only");

    public override void Write(Utf8JsonWriter writer, SerializedLogsResult value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (SerializedLogs blockLogs in value.Blocks)
        {
            for (int i = 0; i < blockLogs.Count; i++)
            {
                writer.WriteRawValue(blockLogs[i], skipInputValidation: true);
            }
        }
        writer.WriteEndArray();
    }
}
