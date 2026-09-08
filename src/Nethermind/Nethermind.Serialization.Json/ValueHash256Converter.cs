// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json;

public class ValueHash256Converter(bool strictHexFormat = false) : JsonConverter<ValueHash256>
{
    private readonly bool _strictHexFormat = strictHexFormat;

    [SkipLocalsInit]
    public override ValueHash256 Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        if (ByteArrayConverter.TryConvertToExactLength(ref reader, bytes, _strictHexFormat))
        {
            return new ValueHash256(bytes);
        }

        byte[] bytesArray = ByteArrayConverter.ConvertData(ref reader, _strictHexFormat)
            ?? throw new JsonException($"Cannot deserialize null into non-nullable {nameof(ValueHash256)}.");
        if (bytesArray.Length != ValueHash256.MemorySize)
        {
            throw new JsonException(
                $"Invalid {nameof(ValueHash256)} length: expected {ValueHash256.MemorySize} bytes, got {bytesArray.Length}.");
        }

        return new ValueHash256(bytesArray);
    }

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        ValueHash256 keccak,
        JsonSerializerOptions options) => HexWriter.WriteFixed32HexRawValue(writer, keccak.Bytes);
}
