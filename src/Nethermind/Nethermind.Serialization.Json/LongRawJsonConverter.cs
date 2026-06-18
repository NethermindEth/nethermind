// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class LongRawJsonConverter : JsonConverter<long>
{
    public override long Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetInt64();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? s = reader.GetString();
            if (s is not null)
            {
                if (s.StartsWith("0x"))
                {
                    return long.Parse(s.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier);
                }
                return long.Parse(s, System.Globalization.NumberStyles.Integer);
            }
        }

        throw new JsonException();
    }

    public override void Write(
        Utf8JsonWriter writer,
        long value,
        JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
