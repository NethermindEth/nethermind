// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Json;

/// <summary>Converts byte-array arrays as JSON arrays of hex strings.</summary>
/// <remarks>Reuses <see cref="ByteArrayConverter"/> for each element while avoiding the generic collection converter pipeline for hot engine payload fields.</remarks>
public sealed class ByteArrayArrayConverter : JsonConverter<byte[][]>
{
    /// <inheritdoc/>
    public override byte[][]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
        {
            ThrowJsonException();
        }

        using ArrayPoolListRef<byte[]> values = new(reader.TokenType == JsonTokenType.EndArray ? 0 : 4);
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(ByteArrayConverter.Convert(ref reader)!);
            if (!reader.Read())
            {
                ThrowJsonException();
            }
        }

        return values.ToArray();
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, byte[][] value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }

        writer.WriteStartArray();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is byte[] item)
            {
                ByteArrayConverter.Convert(writer, item, skipLeadingZeros: false);
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        writer.WriteEndArray();
    }

    [DoesNotReturn]
    private static void ThrowJsonException() => throw new JsonException();
}
