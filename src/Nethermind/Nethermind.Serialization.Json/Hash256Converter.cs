// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;

namespace Nethermind.Serialization.Json;

public class Hash256Converter(bool strictHexFormat = false) : JsonConverter<Hash256?>
{
    private readonly bool _strictHexFormat = strictHexFormat;

    [SkipLocalsInit]
    public override Hash256? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        Span<byte> bytes = stackalloc byte[Hash256.Size];
        if (ByteArrayConverter.TryConvertToExactLength(ref reader, bytes, _strictHexFormat))
        {
            return new Hash256(bytes);
        }

        byte[]? bytesArray = ByteArrayConverter.ConvertData(ref reader, _strictHexFormat);
        return bytesArray switch
        {
            null => null,
            { Length: Hash256.Size } => new Hash256(bytesArray),
            _ => ThrowInvalidLength(bytesArray.Length)
        };
    }

    [DoesNotReturn, StackTraceHidden]
    private static Hash256 ThrowInvalidLength(int length) =>
        throw new SafePublicMessageFormatException($"hex string has length {length * 2}, want {Hash256.Size * 2} for common.Hash");

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        Hash256? keccak,
        JsonSerializerOptions options)
    {
        if (keccak is null)
        {
            writer.WriteNullValue();
            return;
        }

        HexWriter.WriteFixed32HexRawValue(writer, keccak.ValueHash256.Bytes);
    }

    [SkipLocalsInit]
    public override Hash256 ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Span<byte> bytes = stackalloc byte[Hash256.Size];
        if (ByteArrayConverter.TryConvertToExactLength(ref reader, bytes, _strictHexFormat))
        {
            return new Hash256(bytes);
        }

        byte[]? bytesArray = ByteArrayConverter.ConvertData(ref reader, _strictHexFormat);
        return bytesArray is null ? throw new JsonException("Invalid hash property name.") : new Hash256(bytesArray);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Hash256 value, JsonSerializerOptions options) => writer.WritePropertyName(value.ToString());
}
