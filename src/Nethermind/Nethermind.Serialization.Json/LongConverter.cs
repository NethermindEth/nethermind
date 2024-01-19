// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;

namespace Nethermind.Serialization.Json
{
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Buffers.Text;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class LongConverter : JsonConverter<long>
    {
        public static long FromString(string s)
        {
            if (s is null)
            {
                throw new JsonException("null cannot be assigned to long");
            }

            if (s == "0x0")
            {
                return 0L;
            }

            if (s.StartsWith("0x0"))
            {
                return long.Parse(s.AsSpan(2), NumberStyles.AllowHexSpecifier);
            }

            if (s.StartsWith("0x"))
            {
                Span<char> withZero = new(new char[s.Length - 1]);
                withZero[0] = '0';
                s.AsSpan(2).CopyTo(withZero.Slice(1));
                return long.Parse(withZero, NumberStyles.AllowHexSpecifier);
            }

            return long.Parse(s, NumberStyles.Integer);
        }

        public static long FromString(ReadOnlySpan<byte> s)
        {
            if (s.Length == 0)
            {
                throw new JsonException("null cannot be assigned to long");
            }

            if (s.SequenceEqual("0x0"u8))
            {
                return 0L;
            }

            long value;
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
            long value,
            JsonSerializerOptions options)
        {
            switch (ForcedNumberConversion.GetFinalConversion())
            {
                case NumberConversion.Hex:
                    if (value == 0)
                    {
                        writer.WriteRawValue("\"0x0\""u8, skipInputValidation: true);
                    }
                    else
                    {
                        Span<byte> bytes = stackalloc byte[8];
                        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
                        ByteArrayConverter.Convert(writer, bytes, skipLeadingZeros: true);
                    }
                    break;
                case NumberConversion.Decimal:
                    writer.WriteStringValue(value == 0 ? "0" : value.ToString(CultureInfo.InvariantCulture));
                    break;
                case NumberConversion.Raw:
                    writer.WriteNumberValue(value);
                    break;
                default:
                    throw new NotSupportedException();

            }
        }
    }
}
