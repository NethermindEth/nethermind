// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json
{
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Buffers.Text;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class ULongConverter : JsonConverter<ulong>
    {
        public static ulong FromString(ReadOnlySpan<byte> s)
        {
            if (s.Length == 0)
            {
                throw new JsonException("null cannot be assigned to long");
            }

            if (s.SequenceEqual("0x0"u8))
            {
                return 0uL;
            }

            ulong value;
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

            throw new JsonException("hex to long");
        }

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

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            ulong value,
            JsonSerializerOptions options)
        {
            if (value == 0)
            {
                writer.WriteRawValue("\"0x0\""u8, skipInputValidation: true);
            }
            else
            {
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
                ByteArrayConverter.Convert(writer, bytes, skipLeadingZeros: true);
            }
        }
    }
}
