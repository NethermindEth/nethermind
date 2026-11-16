// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

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

    public override ValueHash256 ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    [SkipLocalsInit]
    public override void WriteAsPropertyName(Utf8JsonWriter writer, ValueHash256 value, JsonSerializerOptions options)
    {
        Unsafe.SkipInit(out HexWriter.HexBuffer72 hashBuffer);
        Span<byte> hashBytes = MemoryMarshal.AsBytes((Span<ulong>)hashBuffer);
        hashBytes[0] = (byte)'0';
        hashBytes[1] = (byte)'x';
        HexEncoder.Encode32Bytes(ref hashBytes[2], ref MemoryMarshal.GetReference(value.Bytes));
        writer.WritePropertyName(hashBytes[..(ValueHash256.MemorySize * 2 + 2)]);
    }
}
