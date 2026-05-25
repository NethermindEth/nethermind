// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Buffers;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public sealed class CappedArrayIntJsonConverter : JsonConverter<CappedArray<int>>
{
    public override CappedArray<int> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected JSON array");

        List<int> values = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.GetInt32());
        }
        int[] arr = values.ToArray();
        return new CappedArray<int>(arr, arr.Length);
    }

    public override void Write(Utf8JsonWriter writer, CappedArray<int> value, JsonSerializerOptions options)
    {
        if (value.IsNull) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        ReadOnlySpan<int> span = value.AsSpan();
        for (int i = 0; i < span.Length; i++) writer.WriteNumberValue(span[i]);
        writer.WriteEndArray();
    }
}
