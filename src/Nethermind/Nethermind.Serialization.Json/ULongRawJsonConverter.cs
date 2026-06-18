// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class ULongRawJsonConverter : JsonConverter<ulong>
{
    public override ulong Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetUInt64();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? s = reader.GetString();
            if (s is not null)
            {
                if (s.StartsWith("0x"))
                {
                    return ulong.Parse(s.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier);
                }
                return ulong.Parse(s, System.Globalization.NumberStyles.Integer);
            }
        }

        throw new JsonException();
    }

    public override void Write(
        Utf8JsonWriter writer,
        ulong value,
        JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
