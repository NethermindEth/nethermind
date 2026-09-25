// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Reads and writes <c>Dictionary&lt;Address, TValue&gt;</c> with the keys parsed straight from UTF-8.
/// </summary>
/// <remarks>
/// System.Text.Json's dictionary converter materializes every key as a string (kept for error paths) before handing it
/// to the key converter, even when that converter parses UTF-8 directly. State overrides can carry hundreds of accounts
/// per call. Keys are read and written by <see cref="AddressConverter"/> exactly as before; a repeated key keeps the
/// last value, as System.Text.Json does.
/// </remarks>
public sealed class AddressKeyedDictionaryConverterFactory(bool strictHexFormat = false) : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>)
        && typeToConvert.GetGenericArguments()[0] == typeof(Address);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        // The argument goes in an array: a bare bool binds to the (Type, bool nonPublic) overload.
        (JsonConverter)Activator.CreateInstance(
            typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[1]),
            [strictHexFormat])!;

    private sealed class Converter<TValue>(bool strictHexFormat) : JsonConverter<Dictionary<Address, TValue>>
    {
        public override Dictionary<Address, TValue>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException($"Expected an object for {typeToConvert.Name}.");

            Dictionary<Address, TValue> result = [];
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return result;
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected an address property name.");

                Address key = AddressConverter.ReadAddressPropertyName(ref reader, strictHexFormat);
                if (!reader.Read()) break;
                result[key] = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
            }

            throw new JsonException($"Unterminated object for {typeToConvert.Name}.");
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<Address, TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<Address, TValue> entry in value)
            {
                AddressConverter.WriteAddressPropertyName(writer, entry.Key);
                JsonSerializer.Serialize(writer, entry.Value, options);
            }

            writer.WriteEndObject();
        }
    }
}
