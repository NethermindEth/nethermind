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
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return ReadOperation(document.RootElement, options)!;
    }

    internal static ParityVmOperationTrace? ReadOperation(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        JsonElement execution = value.GetProperty("ex");
        ParityVmOperationTrace operation = new()
        {
            Cost = value.GetProperty("cost").GetUInt64(),
            Pc = value.GetProperty("pc").GetInt32(),
            Sub = ParityVmTraceConverter.ReadTrace(value.GetProperty("sub"), options),
            Halted = execution.ValueKind == JsonValueKind.Null,
        };
        if (!operation.Halted)
        {
            JsonElement memory = execution.GetProperty("mem");
            if (memory.ValueKind != JsonValueKind.Null)
            {
                operation.Memory = new ParityMemoryChangeTrace
                {
                    Data = memory.GetProperty("data").Deserialize<byte[]>(options)!,
                    Offset = memory.GetProperty("off").Deserialize<long>(options),
                };
            }
            operation.Push = execution.GetProperty("push").Deserialize<byte[][]>(options);
            JsonElement store = execution.GetProperty("store");
            if (store.ValueKind != JsonValueKind.Null)
            {
                operation.Store = new ParityStorageChangeTrace
                {
                    Key = store.GetProperty("key").Deserialize<byte[]>(options)!,
                    Value = store.GetProperty("val").Deserialize<byte[]>(options)!,
                };
            }
            operation.Used = execution.GetProperty("used").GetUInt64();
        }
        return operation;
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
        JsonSerializer.Serialize(writer, value.Sub, options);

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
            JsonSerializer.Serialize(writer, value.Memory.Data, options);
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
