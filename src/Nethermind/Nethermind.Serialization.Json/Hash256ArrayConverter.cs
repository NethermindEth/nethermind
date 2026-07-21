// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
    private const int InitialEwma = 32;

    private int _ewma = InitialEwma;

    /// <inheritdoc/>
    public override Hash256?[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
        {
            ThrowJsonException();
        }

        if (reader.TokenType == JsonTokenType.EndArray) return [];

        // Pow2 rounding of EMA matches ArrayPool's bucket sizes and gives natural slack
        // (anything in (N/2, N] rounds to N). Rent then snaps to its own bucket >= request.
        int seed = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, Volatile.Read(ref _ewma)));
        using ArrayPoolListRef<Hash256?> values = new(seed);
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

        Hash256?[] result = values.ToArray();
        int count = values.Count;

        // EMA alpha = 1/8. Race-tolerant: Volatile is enough; lost updates only slow convergence.
        int prev = Volatile.Read(ref _ewma);
        Volatile.Write(ref _ewma, ((prev * 7) + count) >> 3);

        return result;
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
