// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public class ParityVmTraceConverter : JsonConverter<ParityVmTrace>
{
    public override void Write(Utf8JsonWriter writer, ParityVmTrace value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("code"u8);
        JsonSerializer.Serialize(writer, value.Code ?? [], options);
        writer.WritePropertyName("ops"u8);
        JsonSerializer.Serialize(writer, value.Operations, options);
        writer.WriteEndObject();
    }

    public override ParityVmTrace Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadTrace(ref reader, options);

    /// <summary>
    /// Reads a frame and its nested frames through direct calls rather than the serializer, keeping each call depth to a
    /// couple of small stack frames; the reader's max depth bounds the recursion.
    /// </summary>
    internal static ParityVmTrace ReadTrace(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityVmTrace)}.");
        }

        ParityVmTrace value = new();

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("code"u8))
            {
                reader.Read();
                value.Code = JsonSerializer.Deserialize<byte[]>(ref reader, options);
            }
            else if (reader.ValueTextEquals("ops"u8))
            {
                reader.Read();
                // Null until the tracer leaves the frame.
                value.Operations = reader.TokenType == JsonTokenType.Null ? null : ReadOperations(ref reader, options);
            }
            else
            {
                throw new JsonException($"Cannot deserialize {nameof(ParityVmTrace)}.");
            }

            reader.Read();
        }

        return value;
    }

    private static List<ParityVmOperationTrace> ReadOperations(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityVmTrace)}.");
        }

        List<ParityVmOperationTrace> operations = [];

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            operations.Add(reader.TokenType == JsonTokenType.Null ? null : ParityVmOperationTraceConverter.ReadOperation(ref reader, options));
            reader.Read();
        }

        return operations;
    }
}
