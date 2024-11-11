// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
        else if (reader.TokenType == JsonTokenType.String)
        {
            if (!reader.HasValueSequence)
            {
                return ULongConverter.FromString(reader.ValueSpan);
            }
            else
            {
                return ULongConverter.FromString(reader.ValueSequence.ToArray());
            }
        }

        throw new JsonException();
    }

    public override void Write(
        Utf8JsonWriter writer,
        ulong value,
        JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
