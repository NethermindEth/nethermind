// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json;

public class ValueHash256Converter : JsonConverter<ValueHash256>
{
    public override ValueHash256 Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        byte[]? bytes = ByteArrayConverter.Convert(ref reader);
        return bytes is null ? null : new ValueHash256(bytes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        ValueHash256 keccak,
        JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, keccak.Bytes, skipLeadingZeros: false);
    }
}
