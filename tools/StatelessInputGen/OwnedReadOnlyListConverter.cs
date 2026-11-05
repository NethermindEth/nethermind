// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

namespace Nethermind.StatelessInputGen;

public class OwnedReadOnlyListConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType)
            return false;

        return typeToConvert.GetGenericTypeDefinition() == typeof(IOwnedReadOnlyList<>);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type itemType = typeToConvert.GetGenericArguments()[0];

        return (JsonConverter)Activator.CreateInstance(
            typeof(OwnedReadOnlyListConverterInner<>).MakeGenericType(itemType),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            args: null,
            culture: null)!;
    }

    private class OwnedReadOnlyListConverterInner<T> : JsonConverter<IOwnedReadOnlyList<T>>
    {
        public override IOwnedReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null!;

            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"JsonTokenType was of type {reader.TokenType}, only arrays are supported");

            ArrayPoolList<T> list = new(0);

            try
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                        return list;

                    T item = JsonSerializer.Deserialize<T>(ref reader, options)!;
                    list.Add(item);
                }
            }
            catch
            {
                DisposeListItems(list);
                list.Dispose();
                throw;
            }

            DisposeListItems(list);
            list.Dispose();

            throw new JsonException("Incomplete JSON array.");
        }

        public override void Write(Utf8JsonWriter writer, IOwnedReadOnlyList<T> value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();

            for (int i = 0; i < value.Count; i++)
                JsonSerializer.Serialize(writer, value[i], options);

            writer.WriteEndArray();
        }

        private static void DisposeListItems(ArrayPoolList<T> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is IDisposable disposable)
                    disposable.Dispose();
            }
        }
    }
}
