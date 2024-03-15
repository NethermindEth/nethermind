// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;


public class DictionaryUInt256KeyConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType)
        {
            return false;
        }

        if (typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            || typeToConvert.GetGenericTypeDefinition() == typeof(IDictionary<,>))
        {
            return typeToConvert.GetGenericArguments()[0] == typeof(UInt256);
        }

        return false;
    }

    public override JsonConverter CreateConverter(
        Type type,
        JsonSerializerOptions options)
    {
        Type keyType = type.GetGenericArguments()[0];
        Type valueType = type.GetGenericArguments()[1];

        JsonConverter converter = (JsonConverter)Activator.CreateInstance(
            typeof(DictionaryUInt256KeyConverterInner<,>).MakeGenericType(
                new Type[] { keyType, valueType }),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            args: new object[] { options },
            culture: null)!;

        return converter;
    }

    private class DictionaryUInt256KeyConverterInner<TKey, TValue> :
        JsonConverter<Dictionary<TKey, TValue>> where TKey : notnull
    {
        private readonly JsonConverter<TValue> _valueConverter;

        public DictionaryUInt256KeyConverterInner(JsonSerializerOptions options)
        {
            _valueConverter = (JsonConverter<TValue>)options.GetConverter(typeof(TValue));
        }

        public override Dictionary<TKey, TValue> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"JsonTokenType was of type {reader.TokenType}, only objects are supported");
            }

            var dictionary = new Dictionary<UInt256, TValue>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("JsonTokenType was not PropertyName");
                }

                var propertyName = reader.GetString();

                if (string.IsNullOrWhiteSpace(propertyName))
                {
                    throw new JsonException("Failed to get property name");
                }

                reader.Read();

                UInt256 key = UInt256.Parse(propertyName);
                dictionary.Add(key, JsonSerializer.Deserialize<TValue>(ref reader, options)!);
            }

            return (Dictionary<TKey, TValue>)(object)dictionary;
        }

        public override void Write(
            Utf8JsonWriter writer,
            Dictionary<TKey, TValue> dictionary,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            foreach ((TKey key, TValue value) in dictionary)
            {
                string propertyName = key.ToString();
                writer.WritePropertyName(propertyName);

                _valueConverter.Write(writer, value, options);
            }

            writer.WriteEndObject();
        }
    }
}

