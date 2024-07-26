// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeTxTraceConverter : JsonConverter<GethLikeTxTrace>
{
    public override GethLikeTxTrace Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(
        Utf8JsonWriter writer,
        GethLikeTxTrace value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else if (value.CustomTracerResult is not null)
        {
            JsonSerializer.Serialize(writer, value.CustomTracerResult, options);
        }
        else
        {
            writer.WriteStartObject();

            writer.WritePropertyName("gas"u8);
            JsonSerializer.Serialize(writer, value.Gas, options);

            writer.WritePropertyName("failed"u8);
            JsonSerializer.Serialize(writer, value.Failed, options);

            writer.WritePropertyName("returnValue"u8);
            JsonSerializer.Serialize(writer, value.ReturnValue, options);

            writer.WritePropertyName("structLogs"u8);
            JsonSerializer.Serialize(writer, value.Entries, options);

            writer.WriteEndObject();
        }
    }
}
/*
public class GethLikeTxTraceConverter : JsonConverter<GethLikeTxTrace>
{
    public override GethLikeTxTrace ReadJson(
        JsonReader reader,
        Type objectType,
        GethLikeTxTrace existingValue,
        bool hasExistingValue,
        JsonSerializer serializer) => throw new NotSupportedException();

    public override void WriteJson(JsonWriter writer, GethLikeTxTrace value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
        }
        else if (value.CustomTracerResult is not null)
        {
            serializer.Serialize(writer, value.CustomTracerResult);
        }
        else
        {
            writer.WriteStartObject();

            writer.WriteProperty("gas", value.Gas, serializer);
            writer.WriteProperty("failed", value.Failed);
            writer.WriteProperty("returnValue", value.ReturnValue, serializer);

            writer.WritePropertyName("structLogs");
            WriteEntries(writer, value.Entries, serializer);

            writer.WriteEndObject();
        }
    }

    private static void WriteEntries(JsonWriter writer, List<GethTxTraceEntry> entries, JsonSerializer _)
    {
        writer.WriteStartArray();
        foreach (GethTxTraceEntry entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteProperty("pc", entry.ProgramCounter);
            writer.WriteProperty("op", entry.Opcode);
            writer.WriteProperty("gas", entry.Gas);
            writer.WriteProperty("gasCost", entry.GasCost);
            writer.WriteProperty("depth", entry.Depth);
            writer.WriteProperty("error", entry.Error);
            writer.WritePropertyName("stack");
            writer.WriteStartArray();
            foreach (string stackItem in entry.Stack)
            {
                writer.WriteValue(stackItem);
            }

            writer.WriteEndArray();

            if (entry.Memory is not null)
            {
                writer.WritePropertyName("memory");
                writer.WriteStartArray();
                foreach (string memory in entry.Memory)
                {
                    writer.WriteValue(memory);
                }
                writer.WriteEndArray();
            }

            if (entry.Storage is not null)
            {
                writer.WritePropertyName("storage");
                writer.WriteStartObject();

                foreach (var item in entry.Storage.OrderBy(s => s.Key))
                {
                    writer.WriteProperty(item.Key, item.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
*/
