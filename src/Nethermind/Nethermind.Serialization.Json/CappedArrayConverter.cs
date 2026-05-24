// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Json;

/// <summary>
/// JSON converter factory for <see cref="CappedArray{T}"/>. Serializes the valid prefix
/// (<see cref="CappedArray{T}.AsSpan"/>) as a JSON array whose elements go through whatever
/// converter is registered for <typeparamref name="T"/>. Lets pooling code carry an
/// oversized <see cref="System.Buffers.ArrayPool{T}"/>-rented buffer plus an explicit
/// length without leaking the over-allocation into the wire output.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CappedArray{T}"/> requires <c>T : struct</c>, so the factory is restricted
/// to closed generic types where the element type is a value type.
/// </para>
/// <para>
/// For <c>CappedArray&lt;byte&gt;</c>, this factory produces a JSON array of numbers
/// (one per byte), <em>not</em> the Ethereum-style <c>"0x..."</c> hex string that
/// <see cref="ByteArrayConverter"/> produces for <c>byte[]</c>. Today no JSON-serialized
/// surface uses <c>CappedArray&lt;byte&gt;</c> (the type is internal to Trie/RLP code
/// paths), but if that changes a dedicated hex-style converter for that closed type
/// should be added and registered <em>before</em> this factory.
/// </para>
/// </remarks>
public class CappedArrayConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(CappedArray<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type element = typeToConvert.GetGenericArguments()[0];
        Type converter = typeof(CappedArrayConverter<>).MakeGenericType(element);
        return (JsonConverter)Activator.CreateInstance(converter)!;
    }
}

internal sealed class CappedArrayConverter<T> : JsonConverter<CappedArray<T>> where T : struct
{
    public override CappedArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected JSON array");

        // CappedArray is most useful for short value-type sequences (e.g. trace-address
        // arrays sized by EVM call depth, typically < 32). Buffering into a List is fine
        // at that scale.
        List<T> values = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            T value = JsonSerializer.Deserialize<T>(ref reader, options)!;
            values.Add(value);
        }
        T[] arr = values.ToArray();
        return new CappedArray<T>(arr, arr.Length);
    }

    public override void Write(Utf8JsonWriter writer, CappedArray<T> value, JsonSerializerOptions options)
    {
        if (value.IsNull)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        ReadOnlySpan<T> span = value.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            JsonSerializer.Serialize(writer, span[i], options);
        }
        writer.WriteEndArray();
    }
}
