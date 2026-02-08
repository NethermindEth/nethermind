// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

public class Hash256Converter : JsonConverter<Hash256>
{
    private readonly bool _strictHexFormat;

    public Hash256Converter(bool strictHexFormat = false)
    {
        _strictHexFormat = strictHexFormat;
    }

    public override Hash256? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {

        byte[]? bytes = ByteArrayConverter.Convert(ref reader, _strictHexFormat);
        return bytes is null ? null : new Hash256(bytes);
    }

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        Hash256 keccak,
        JsonSerializerOptions options)
    {
        // Fixed-size fast path for 32-byte hashes: "0x" + 64 hex chars = 66 bytes
        Span<byte> hex = stackalloc byte[66];
        hex[0] = (byte)'0';
        hex[1] = (byte)'x';
        keccak.Bytes.OutputBytesToByteHex(hex[2..], extraNibble: false);
        writer.WriteStringValue(hex);
    }

    // Methods needed to ser/de dictionary keys
    public override Hash256 ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte[]? bytes = ByteArrayConverter.Convert(ref reader, _strictHexFormat);
        return bytes is null ? null! : new Hash256(bytes);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Hash256 value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.ToString());
    }
}
