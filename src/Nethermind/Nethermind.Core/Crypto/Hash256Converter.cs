// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json;

public class Hash256Converter : JsonConverter<Hash256>
{
    public override Hash256? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        byte[]? bytes = ByteArrayConverter.Convert(ref reader);
        return bytes is null ? null : new Hash256(bytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        Hash256 keccak,
        JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, keccak.Bytes, skipLeadingZeros: false);
    }
}
