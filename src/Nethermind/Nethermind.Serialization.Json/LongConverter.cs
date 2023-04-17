// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class LongConverter : JsonConverter<long>
    {
        private readonly NumberConversion _conversion;

        public LongConverter()
            : this(NumberConversion.Hex)
        {
        }

        public LongConverter(NumberConversion conversion)
        {
            _conversion = conversion;
        }

        public override void WriteJson(JsonWriter writer, long value, JsonSerializer serializer)
        {
            switch (_conversion)
            {
                case NumberConversion.Hex:
                    writer.WriteValue(value == 0L ? "0x0" : value.ToHexString(true));
                    break;
                case NumberConversion.Decimal:
                    writer.WriteValue(value == 0 ? "0" : value.ToString());
                    break;
                case NumberConversion.Raw:
                    writer.WriteValue(value);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        public override long ReadJson(JsonReader reader, Type objectType, long existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return reader.Value is long || reader.Value is int
                ? (long)reader.Value
                : FromString(reader.Value?.ToString());
        }

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
    }
}
