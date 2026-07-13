// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.Data;

internal static class GetBlobsV4Limits
{
    public const int MaxBlobVersionedHashes = 128;
}

internal sealed class BlobVersionedHashesV4Converter : JsonConverter<byte[][]>
{
    public override byte[][]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array of blob versioned hashes.");
        }

        using ArrayPoolListRef<byte[]> hashes = new(16);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return hashes.Count == 0 ? [] : hashes.ToArray();
            }

            if (hashes.Count == GetBlobsV4Limits.MaxBlobVersionedHashes)
            {
                throw new JsonException($"Blob versioned hash count must not exceed {GetBlobsV4Limits.MaxBlobVersionedHashes}.");
            }

            if (!HasExactHexLength(ref reader, Hash256.Size))
            {
                throw new JsonException($"Blob versioned hashes must be exactly {Hash256.Size} bytes.");
            }

            byte[]? hash = ByteArrayConverter.ConvertData(ref reader, strictHexFormat: true);
            if (hash is not { Length: Hash256.Size })
            {
                throw new JsonException($"Blob versioned hashes must be exactly {Hash256.Size} bytes.");
            }

            hashes.Add(hash);
        }

        throw new JsonException("Unterminated blob versioned hash array.");
    }

    public override void Write(Utf8JsonWriter writer, byte[][] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        for (int i = 0; i < value.Length; i++)
        {
            ByteArrayConverter.Convert(writer, value[i], skipLeadingZeros: false);
        }

        writer.WriteEndArray();
    }

    private static bool HasExactHexLength(ref Utf8JsonReader reader, int byteLength)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return false;
        }

        long encodedLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        return encodedLength == 2L + (2L * byteLength);
    }
}

internal sealed class BlobCellBitArrayConverter : JsonConverter<BitArray>
{
    public override BitArray Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        long encodedLength = reader.TokenType == JsonTokenType.String
            ? reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length
            : 0;
        if (encodedLength != 2L + (2L * BlobCellMask.FixedByteLength))
        {
            throw new JsonException($"Blob cell bit arrays must be exactly {BlobCellMask.FixedByteLength} bytes.");
        }

        byte[]? bytes = ByteArrayConverter.ConvertData(ref reader, strictHexFormat: true);
        return bytes is { Length: BlobCellMask.FixedByteLength }
            ? new BitArray(bytes)
            : throw new JsonException($"Blob cell bit arrays must be exactly {BlobCellMask.FixedByteLength} bytes.");
    }

    public override void Write(Utf8JsonWriter writer, BitArray value, JsonSerializerOptions options)
    {
        if (value.Length != BlobCellMask.CellCount)
        {
            throw new JsonException($"Blob cell bit arrays must contain exactly {BlobCellMask.CellCount} bits.");
        }

        Span<byte> bytes = stackalloc byte[BlobCellMask.FixedByteLength];
        for (int i = 0; i < value.Length; i++)
        {
            if (value.Get(i))
            {
                bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        ByteArrayConverter.Convert(writer, bytes, skipLeadingZeros: false);
    }
}
