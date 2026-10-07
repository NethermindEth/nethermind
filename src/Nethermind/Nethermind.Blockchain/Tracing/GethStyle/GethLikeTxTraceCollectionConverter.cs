// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

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

        List<GethLikeTxTrace> traces = [];

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
            string? error = null;

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
                    trace = TypeInfoJsonSerializer.Deserialize<GethLikeTxTrace>(ref reader, options);
                    continue;
                }

                if (reader.ValueTextEquals("error"u8))
                {
                    reader.Read();
                    error = reader.GetString();
                    continue;
                }

                if (reader.ValueTextEquals("txHash"u8))
                {
                    reader.Read();
                    txHash = reader.TokenType == JsonTokenType.Null ? null : TypeInfoJsonSerializer.Deserialize<Hash256>(ref reader, options);
                    continue;
                }

                throw new JsonException($"Unexpected property: {reader.GetString()}");
            }

            if (error is not null)
            {
                if (trace is not null) throw new JsonException("Both result and error properties");
                trace = new GethLikeTxTrace { TraceError = error };
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

        foreach (GethLikeTxTrace trace in value.Traces)
        {
            WriteEntry(writer, trace, options);
        }

        writer.WriteEndArray();
    }

    internal static void WriteEntry(Utf8JsonWriter writer, GethLikeTxTrace trace, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (trace.TraceError is { } error) writer.WriteString("error"u8, error);
        else
        {
            writer.WritePropertyName("result"u8);
            TypeInfoJsonSerializer.Serialize(writer, trace, options);
        }
        writer.WritePropertyName("txHash"u8);
        TypeInfoJsonSerializer.Serialize(writer, trace.TxHash, options);
        writer.WriteEndObject();
    }
}
