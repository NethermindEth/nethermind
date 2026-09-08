// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Core.BlockAccessLists;

public class StorageChangesByIndexConverter : JsonConverter<StorageChange[]>
{
    public override StorageChange[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected object for StorageChange map.");
        }

        List<StorageChange> result = [];
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException();
            }

            // Property name is the index — we read it from the StorageChange itself, not the key.
            reader.Read();
            StorageChange change = JsonSerializer.Deserialize<StorageChange>(ref reader, options);
            result.Add(change);
        }

        return [.. result];
    }

    public override void Write(Utf8JsonWriter writer, StorageChange[] value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (StorageChange change in value)
        {
            writer.WritePropertyName(change.Index.ToString(CultureInfo.InvariantCulture));
            JsonSerializer.Serialize(writer, change, options);
        }
        writer.WriteEndObject();
    }
}
