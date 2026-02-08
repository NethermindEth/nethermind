// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

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
        WriteHashHex(writer, keccak.Bytes);
    }

    /// <summary>
    /// SIMD-accelerated hex encoding for 32-byte hashes.
    /// Writes raw JSON (including quotes) via WriteRawValue to bypass the encoder entirely.
    /// </summary>
    [SkipLocalsInit]
    internal static void WriteHashHex(Utf8JsonWriter writer, ReadOnlySpan<byte> hash)
    {
        // Raw JSON: '"' + "0x" + 64 hex chars + '"' = 68 bytes
        Span<byte> buf = stackalloc byte[68];
        ref byte b = ref MemoryMarshal.GetReference(buf);

        Unsafe.Add(ref b, 0) = (byte)'"';
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, 1), (ushort)0x7830); // "0x" LE

        HexWriter.Encode32Bytes(ref Unsafe.Add(ref b, 3), hash);

        Unsafe.Add(ref b, 67) = (byte)'"';

        writer.WriteRawValue(buf, skipInputValidation: true);
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
