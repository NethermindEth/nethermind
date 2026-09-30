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
        if (reader.TokenType == JsonTokenType.Null) return null;
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        return new ParityVmTrace
        {
            Code = value.GetProperty("code").Deserialize<byte[]>(options)!,
            Operations = value.GetProperty("ops").Deserialize<List<ParityVmOperationTrace>>(options)!,
        };
    }
}
