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

    public override ParityVmTrace? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return ReadTrace(document.RootElement, options);
    }

    internal static ParityVmTrace? ReadTrace(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        JsonElement operations = value.GetProperty("ops");
        List<ParityVmOperationTrace>? traces = null;
        if (operations.ValueKind != JsonValueKind.Null)
        {
            traces = new(operations.GetArrayLength());
            foreach (JsonElement operation in operations.EnumerateArray())
            {
                traces.Add(ParityVmOperationTraceConverter.ReadOperation(operation, options)!);
            }
        }
        return new ParityVmTrace
        {
            Code = value.GetProperty("code").Deserialize<byte[]>(options)!,
            Operations = traces!,
        };
    }
}
