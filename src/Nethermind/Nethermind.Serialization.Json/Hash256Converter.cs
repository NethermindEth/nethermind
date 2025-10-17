// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Text.Json.Serialization;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json;

public class Hash256Converter : JsonConverter<Hash256>
{
    private readonly bool _followStandardizationRules;

    public Hash256Converter(bool followStandardizationRules = false)
    {
        _followStandardizationRules = followStandardizationRules;
    }

    public override Hash256? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {

        byte[]? bytes = ByteArrayConverter.Convert(ref reader, _followStandardizationRules);
        return bytes is null ? null : new Hash256(bytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        Hash256 keccak,
        JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, keccak.Bytes, skipLeadingZeros: false);
    }

    // Methods needed to ser/de dictionary keys
    public override Hash256 ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte[]? bytes = ByteArrayConverter.Convert(ref reader, _followStandardizationRules);
        return bytes is null ? null! : new Hash256(bytes);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Hash256 value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.ToString());
    }
}
