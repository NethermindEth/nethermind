// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Json;

public sealed class CappedArrayJsonConverter<T> : JsonConverter<CappedArray<T>> where T : struct
{
    public override CappedArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected JSON array");

        JsonConverter<T> elementConverter = (JsonConverter<T>)options.GetConverter(typeof(T));

        T[] buffer = ArrayPool<T>.Shared.Rent(16);
        int count = 0;
        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count == buffer.Length)
                {
                    T[] grown = ArrayPool<T>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, count).CopyTo(grown);
                    ArrayPool<T>.Shared.Return(buffer);
                    buffer = grown;
                }
                buffer[count++] = elementConverter.Read(ref reader, typeof(T), options);
            }

            if (count == 0) return CappedArray<T>.Empty;
            T[] result = new T[count];
            buffer.AsSpan(0, count).CopyTo(result);
            return new CappedArray<T>(result, count);
        }
        finally
        {
            ArrayPool<T>.Shared.Return(buffer);
        }
    }

    public override void Write(Utf8JsonWriter writer, CappedArray<T> value, JsonSerializerOptions options)
    {
        if (value.IsNull) { writer.WriteNullValue(); return; }
        JsonConverter<T> elementConverter = (JsonConverter<T>)options.GetConverter(typeof(T));
        writer.WriteStartArray();
        ReadOnlySpan<T> span = value.AsSpan();
        for (int i = 0; i < span.Length; i++) elementConverter.Write(writer, span[i], options);
        writer.WriteEndArray();
    }
}

/// <summary>
/// CappedArray&lt;byte&gt; specializes to hex-string serialization (e.g. <c>"0xabcd"</c>),
/// matching <see cref="ByteArrayConverter"/>'s wire format.
/// </summary>
public sealed class CappedArrayByteJsonConverter : JsonConverter<CappedArray<byte>>
{
    public override CappedArray<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        byte[]? bytes = ByteArrayConverter.Convert(ref reader);
        return bytes is null ? CappedArray<byte>.Empty : new CappedArray<byte>(bytes);
    }

    public override void Write(Utf8JsonWriter writer, CappedArray<byte> value, JsonSerializerOptions options)
    {
        if (value.IsNull) { writer.WriteNullValue(); return; }
        ByteArrayConverter.Convert(writer, value.AsSpan(), skipLeadingZeros: false);
    }
}
