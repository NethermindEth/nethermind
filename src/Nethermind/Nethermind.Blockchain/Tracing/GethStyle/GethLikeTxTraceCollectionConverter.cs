// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxTraceCollectionConverter : JsonConverter<GethLikeTxTraceCollection>
{
    public override GethLikeTxTraceCollection Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected start of array");
        }

        var traces = new List<GethLikeTxTrace>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected start of object");
            }

            GethLikeTxTrace trace = null;
            Hash256? txHash = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Expected property name");
                }

                if (reader.ValueTextEquals("result"u8))
                {
                    reader.Read();
                    trace = JsonSerializer.Deserialize<GethLikeTxTrace>(ref reader, options);
                    continue;
                }

                if (reader.ValueTextEquals("txHash"u8))
                {
                    reader.Read();
                    txHash = reader.TokenType == JsonTokenType.Null ? null : JsonSerializer.Deserialize<Hash256>(ref reader, options);
                    continue;
                }

                throw new JsonException($"Unexpected property: {reader.GetString()}");
            }

            if (trace is null)
            {
                throw new JsonException("Missing result property");
            }

            trace.TxHash = txHash;

            traces.Add(trace);
        }

        return new GethLikeTxTraceCollection(traces);
    }

    public override void Write(
        Utf8JsonWriter writer,
        GethLikeTxTraceCollection? value,
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
