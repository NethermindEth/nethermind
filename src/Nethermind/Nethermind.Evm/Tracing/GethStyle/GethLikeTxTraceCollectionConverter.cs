// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeTxTraceCollectionConverter : JsonConverter<GethLikeTxTraceCollection>
{
    public override GethLikeTxTraceCollection Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(
        Utf8JsonWriter writer,
        GethLikeTxTraceCollection value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();

        foreach (var trace in value.Traces)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("result"u8);
            JsonSerializer.Serialize(writer, trace, options);

            writer.WritePropertyName("txHash"u8);
            JsonSerializer.Serialize(writer, trace.TxHash, options);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
