// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json;

/// <summary>Converts JSON arrays of <c>0x</c>-prefixed 32-byte hex strings to and from <see cref="Hash256"/> arrays.</summary>
/// <remarks>
/// Specialised over <see cref="ByteArrayArrayConverter"/> for engine-API fields whose elements are fixed
/// 32-byte hashes (e.g. <c>blobVersionedHashes</c>): each element decodes straight into a stack-allocated
/// span via <see cref="ByteArrayConverter.TryConvertToExactLength"/> and is wrapped into a single
/// <see cref="Hash256"/> rather than allocating a fresh <c>byte[32]</c> per element.
/// </remarks>
public sealed class Hash256ArrayConverter : JsonConverter<Hash256?[]>
{
    /// <inheritdoc/>
    public override Hash256?[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
        {
            ThrowJsonException();
        }

        using ArrayPoolListRef<Hash256?> values = new(reader.TokenType == JsonTokenType.EndArray ? 0 : 4);
        Span<byte> buffer = stackalloc byte[Hash256.Size];
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                values.Add(null);
            }
            else if (ByteArrayConverter.TryConvertToExactLength(ref reader, buffer))
            {
                values.Add(new Hash256(buffer));
            }
            else
            {
                ThrowJsonException();
            }

            if (!reader.Read())
            {
                ThrowJsonException();
            }
        }

        return values.ToArray();
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Hash256?[] value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }

        writer.WriteStartArray();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is { } hash)
            {
                HexWriter.WriteFixed32HexRawValue(writer, hash.ValueHash256.Bytes);
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
