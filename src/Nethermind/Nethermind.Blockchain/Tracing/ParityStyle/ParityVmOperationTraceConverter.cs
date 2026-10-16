// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public class ParityVmOperationTraceConverter : JsonConverter<ParityVmOperationTrace>
{
    public override ParityVmOperationTrace Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => ReadOperation(ref reader, options);

    internal static ParityVmOperationTrace ReadOperation(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityVmOperationTrace)}.");
        }

        ParityVmOperationTrace value = new();

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("cost"u8))
            {
                reader.Read();
                value.Cost = reader.GetUInt64();
            }
            else if (reader.ValueTextEquals("ex"u8))
            {
                reader.Read();
                ReadExecuted(ref reader, value, options);
            }
            else if (reader.ValueTextEquals("pc"u8))
            {
                reader.Read();
                value.Pc = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("sub"u8))
            {
                reader.Read();
                value.Sub = reader.TokenType == JsonTokenType.Null ? null : ParityVmTraceConverter.ReadTrace(ref reader, options);
            }
            else
            {
                // A member from a newer format; skipping it keeps the block readable after a downgrade.
                reader.Skip();
            }

            reader.Read();
        }

        return value;
    }

    private static void ReadExecuted(ref Utf8JsonReader reader, ParityVmOperationTrace value, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            value.Halted = true;
            return;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityVmOperationTrace)}.");
        }

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("mem"u8))
            {
                reader.Read();
                value.Memory = reader.TokenType == JsonTokenType.Null ? null : ReadMemory(ref reader, options);
            }
            else if (reader.ValueTextEquals("push"u8))
            {
                reader.Read();
                value.Push = TypeInfoJsonSerializer.Deserialize<byte[][]>(ref reader, options);
            }
            else if (reader.ValueTextEquals("store"u8))
            {
                reader.Read();
                value.Store = reader.TokenType == JsonTokenType.Null ? null : ReadStore(ref reader, options);
            }
            else if (reader.ValueTextEquals("used"u8))
            {
                reader.Read();
                value.Used = reader.GetUInt64();
            }
            else
            {
                reader.Skip();
            }

            reader.Read();
        }
    }

    private static ParityMemoryChangeTrace ReadMemory(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityMemoryChangeTrace)}.");
        }

        ParityMemoryChangeTrace memory = new();

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("data"u8))
            {
                reader.Read();
                memory.Data = TypeInfoJsonSerializer.Deserialize<byte[]>(ref reader, options);
            }
            else if (reader.ValueTextEquals("off"u8))
            {
                reader.Read();
                // Older stores hold it as a hex string.
                memory.Offset = TypeInfoJsonSerializer.Deserialize<long>(ref reader, options);
            }
            else
            {
                reader.Skip();
            }

            reader.Read();
        }

        return memory;
    }

    private static ParityStorageChangeTrace ReadStore(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityStorageChangeTrace)}.");
        }

        ParityStorageChangeTrace store = new();

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("key"u8))
            {
                reader.Read();
                store.Key = TypeInfoJsonSerializer.Deserialize<byte[]>(ref reader, options);
            }
            else if (reader.ValueTextEquals("val"u8))
            {
                reader.Read();
                store.Value = TypeInfoJsonSerializer.Deserialize<byte[]>(ref reader, options);
            }
            else
            {
                reader.Skip();
            }

            reader.Read();
        }

        return store;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ParityVmOperationTrace value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteNumber("cost"u8, value.Cost);
        writer.WritePropertyName("ex"u8);
        if (value.Halted)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteExecuted(writer, value, options);
        }

        writer.WriteNumber("pc"u8, value.Pc);
        writer.WritePropertyName("sub"u8);
        TypeInfoJsonSerializer.Serialize(writer, value.Sub, options);

        writer.WriteEndObject();
    }

    private static void WriteExecuted(Utf8JsonWriter writer, ParityVmOperationTrace value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("mem"u8);
        if (value.Memory is not null)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("data"u8);
            TypeInfoJsonSerializer.Serialize(writer, value.Memory.Data, options);
            // A plain number, as the streaming tracer writes it; a long would otherwise serialize as a hex string.
            writer.WriteNumber("off"u8, value.Memory.Offset);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WritePropertyName("push"u8);
        if (value.Push is not null)
        {
            writer.WriteStartArray();
            for (int i = 0; i < value.Push.Length; i++)
            {
                ByteArrayConverter.Convert(writer, value.Push[i], skipLeadingZeros: true);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WritePropertyName("store"u8);
        if (value.Store is not null)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("key"u8);
            ByteArrayConverter.Convert(writer, value.Store.Key, skipLeadingZeros: true);
            writer.WritePropertyName("val"u8);
            ByteArrayConverter.Convert(writer, value.Store.Value, skipLeadingZeros: true);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteNumber("used"u8, value.Used);
        writer.WriteEndObject();
    }
}
