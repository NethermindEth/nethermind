// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class ByteConverter : JsonConverter<byte>
{
    public override byte Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetByte();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return !reader.HasValueSequence
                ? NumericConverterHelper.Parse<byte>(reader.ValueSpan)
                : NumericConverterHelper.Parse<byte>(reader.ValueSequence.ToArray());
        }

        throw new JsonException();
    }

    public override void Write(
        Utf8JsonWriter writer,
        byte value,
        JsonSerializerOptions options) => NumericConverterHelper.Write(writer, value);
}
