// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json;

public class DifficultyBombDelaysConverter : JsonConverter<IDictionary<ulong, ulong>>
{
    public override void Write(Utf8JsonWriter writer, IDictionary<ulong, ulong> value,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override IDictionary<ulong, ulong> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        Dictionary<ulong, ulong> value = [];
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            reader.Read();
            while (reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new ArgumentException("Cannot deserialize dictionary.");
                }

                ReadOnlySpan<byte> keySpan = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                ulong key = NumericConverterHelper.Parse<ulong>(keySpan);

                reader.Read();

                ulong delay;
                if (reader.TokenType == JsonTokenType.String)
                {
                    ReadOnlySpan<byte> valSpan = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                    delay = NumericConverterHelper.Parse<ulong>(valSpan);
                }
                else if (reader.TokenType == JsonTokenType.Number)
                {
                    delay = reader.GetUInt64();
                }
                else
                {
                    throw new ArgumentException("Cannot deserialize dictionary.");
                }

                value.Add(key, delay);

                reader.Read();
            }
        }
        else
        {
            throw new ArgumentException("Cannot deserialize dictionary.");
        }

        return value;
    }
}
