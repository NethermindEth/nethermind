// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Json;

/// <summary>
/// JSON converter factory for <see cref="CappedArray{T}"/>: emits the valid prefix as a
/// JSON array (per-element converter for <typeparamref name="T"/>) without leaking the
/// over-allocated rented buffer length.
/// </summary>
/// <remarks>
/// <c>CappedArray&lt;byte&gt;</c> is routed to <see cref="CappedArrayByteConverter"/> so
/// it emits the Ethereum <c>"0x..."</c> hex string consistent with <c>byte[]</c>; other
/// element types use the generic array-of-elements path.
/// </remarks>
public class CappedArrayConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(CappedArray<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type element = typeToConvert.GetGenericArguments()[0];
        if (element == typeof(byte))
        {
            return new CappedArrayByteConverter();
        }
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

        // Short value-type sequences (e.g. trace-address arrays); List buffering is fine.
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

/// <summary>
/// Hex-string converter for <see cref="CappedArray{Byte}"/> that matches the wire shape of
/// <see cref="ByteArrayConverter"/> (<c>"0x..."</c>) so callers can swap between
/// <c>byte[]</c> and <c>CappedArray&lt;byte&gt;</c> without changing the JSON output.
/// </summary>
internal sealed class CappedArrayByteConverter : JsonConverter<CappedArray<byte>>
{
    public override CappedArray<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte[] bytes = ByteArrayConverter.Convert(ref reader);
        return bytes is null ? default : new CappedArray<byte>(bytes);
    }

    public override void Write(Utf8JsonWriter writer, CappedArray<byte> value, JsonSerializerOptions options)
    {
        if (value.IsNull)
        {
            writer.WriteNullValue();
            return;
        }
        ByteArrayConverter.Convert(writer, value.AsSpan(), skipLeadingZeros: false);
    }
}
