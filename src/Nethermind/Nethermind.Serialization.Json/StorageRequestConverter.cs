// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

public class StorageRequestConverter : JsonConverter<Dictionary<Address, UInt256[]>>
{
    public const int MaxSlots = 1024;

    public override Dictionary<Address, UInt256[]> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected object, got {reader.TokenType}");

        Dictionary<Address, UInt256[]> result = new();
        int totalSlots = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name");

            Address address = new(ByteArrayConverter.Convert(ref reader) ?? throw new JsonException("Invalid address property name"));

            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Expected array of storage slots");

            List<UInt256> slots = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (totalSlots >= MaxSlots)
                    throw new JsonException($"too many slots (max {MaxSlots})");
                slots.Add(JsonSerializer.Deserialize<UInt256>(ref reader, options));
                totalSlots++;
            }
            result[address] = slots.ToArray();
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<Address, UInt256[]> value, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(StorageRequestConverter)} is deserialize-only");
}
