// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.ParityStyle
{
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
            else if (reader.TokenType == JsonTokenType.String)
            {
                if (!reader.HasValueSequence)
                {
                    return LongConverter.FromString(reader.ValueSpan);
                }
                else
                {
                    return LongConverter.FromString(reader.ValueSequence.ToArray());
                }
            }

            throw new JsonException();
        }

        public override void Write(
            Utf8JsonWriter writer,
            long value,
            JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
