// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json
{
    using System.Buffers;
    using System.Buffers.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class IntConverter : JsonConverter<int>
    {
        private static int FromString(ReadOnlySpan<byte> s)
        {
            if (s.Length == 0)
            {
                throw new JsonException("null cannot be assigned to long");
            }

            if (s.SequenceEqual("0x0"u8))
            {
                return 0;
            }

            int value;
            if (s.StartsWith("0x"u8))
            {
                s = s.Slice(2);
                if (Utf8Parser.TryParse(s, out value, out _, 'x'))
                {
                    return value;
                }
            }
            else if (Utf8Parser.TryParse(s, out value, out _))
            {
                return value;
            }

            throw new JsonException("hex to int");
        }

        public override int Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetInt32();
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                if (!reader.HasValueSequence)
                {
                    return FromString(reader.ValueSpan);
                }
                else
                {
                    return FromString(reader.ValueSequence.ToArray());
                }
            }

            throw new JsonException();
        }

        public override void Write(
            Utf8JsonWriter writer,
            int value,
            JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
