// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Deserializes <see cref="Dictionary{TKey,TValue}"/> keyed by <see cref="Address"/> or <see cref="UInt256"/>
/// straight from the UTF-8 property name.
/// </summary>
/// <remarks>
/// The built-in dictionary converter materializes every property name as a <see cref="string"/> (for its JSON
/// path bookkeeping) before handing it to the key converter, so a state-override map with hundreds of storage
/// keys allocates a string per key that nothing reads. Keys and values are read and written through the
/// converters registered for them, so the JSON shape is unchanged.
/// </remarks>
public sealed class SpanKeyedDictionaryConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType || typeToConvert.GetGenericTypeDefinition() != typeof(Dictionary<,>))
        {
            return false;
        }

        Type keyType = typeToConvert.GetGenericArguments()[0];
        return keyType == typeof(Address) || keyType == typeof(UInt256);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type[] arguments = typeToConvert.GetGenericArguments();
        return (JsonConverter)Activator.CreateInstance(typeof(Inner<,>).MakeGenericType(arguments[0], arguments[1]))!;
    }

    private sealed class Inner<TKey, TValue> : JsonConverter<Dictionary<TKey, TValue>> where TKey : notnull
    {
        private JsonConverter<TKey>? _keyConverter;
        private JsonTypeInfo<TValue>? _valueInfo;

        public override Dictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                ThrowUnexpectedToken(reader.TokenType);
            }

            JsonConverter<TKey> keyConverter = KeyConverter(options);
            JsonTypeInfo<TValue> valueInfo = ValueInfo(options);
            Dictionary<TKey, TValue> dictionary = [];
            while (true)
            {
                if (!reader.Read())
                {
                    ThrowIncompleteObject();
                }

                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return dictionary;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    ThrowUnexpectedToken(reader.TokenType);
                }

                TKey key = keyConverter.ReadAsPropertyName(ref reader, typeof(TKey), options);
                if (!reader.Read())
                {
                    ThrowIncompleteObject();
                }

                // Last duplicate wins, as with the built-in dictionary converter.
                dictionary[key] = JsonSerializer.Deserialize(ref reader, valueInfo)!;
            }
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<TKey, TValue> value, JsonSerializerOptions options)
        {
            JsonConverter<TKey> keyConverter = KeyConverter(options);
            JsonTypeInfo<TValue> valueInfo = ValueInfo(options);
            writer.WriteStartObject();
            foreach (KeyValuePair<TKey, TValue> entry in value)
            {
                keyConverter.WriteAsPropertyName(writer, entry.Key, options);
                JsonSerializer.Serialize(writer, entry.Value, valueInfo);
            }

            writer.WriteEndObject();
        }

        private JsonConverter<TKey> KeyConverter(JsonSerializerOptions options) =>
            _keyConverter ??= (JsonConverter<TKey>)options.GetConverter(typeof(TKey));

        private JsonTypeInfo<TValue> ValueInfo(JsonSerializerOptions options) =>
            _valueInfo ??= (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));

        private static void ThrowUnexpectedToken(JsonTokenType tokenType) =>
            throw new JsonException($"Expected an object, got {tokenType}.");

        private static void ThrowIncompleteObject() =>
            throw new JsonException("Incomplete dictionary object.");
    }
}
