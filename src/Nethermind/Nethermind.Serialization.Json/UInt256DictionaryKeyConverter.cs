// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

public class UInt256DictionaryKeyConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        // Check if this converter can convert the given type
        return typeToConvert.IsGenericType &&
               typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
               typeToConvert.GetGenericArguments()[0] == typeof(UInt256);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type valueType = typeToConvert.GetGenericArguments()[1];
        JsonConverter converter = (JsonConverter)Activator.CreateInstance(
            typeof(UInt256DictionaryConverter<>).MakeGenericType(valueType),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            args: new object[] { options },
            culture: null)!;

        return converter;
    }

    private class UInt256DictionaryConverter<TValue> : JsonConverter<Dictionary<UInt256, TValue>>
    {
        private readonly JsonConverter<TValue> _valueConverter;
        private readonly Type _valueType;

        public UInt256DictionaryConverter(JsonSerializerOptions options)
        {
            // For performance, use existing converter if available
            _valueType = typeof(TValue);
            _valueConverter = (JsonConverter<TValue>)options.GetConverter(typeof(TValue));
        }

        public override Dictionary<UInt256, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException();
            }

            var dictionary = new Dictionary<UInt256, TValue>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return dictionary;
                }

                // Assume the key is a string and convert it to UInt256
                string keyString = reader.GetString()!;
                UInt256 key = UInt256.Parse(keyString);

                reader.Read();

                TValue value;

                if (_valueConverter != null)
                {
                    value = _valueConverter.Read(ref reader, _valueType, options)!;
                }
                else
                {
                    value = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
                }

                dictionary[key] = value;
            }

            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<UInt256, TValue> dictionary, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            foreach (KeyValuePair<UInt256, TValue> kvp in dictionary)
            {
                writer.WritePropertyName(kvp.Key.ToString());

                if (_valueConverter != null)
                {
                    _valueConverter.Write(writer, kvp.Value, options);
                }
                else
                {
                    JsonSerializer.Serialize(writer, kvp.Value, options);
                }
            }

            writer.WriteEndObject();
        }
    }
}
