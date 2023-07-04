// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class ULongConverter : JsonConverter<ulong>
    {
        private readonly NumberConversion _conversion;

        public ULongConverter()
            : this(NumberConversion.Hex)
        {
        }

        public ULongConverter(NumberConversion conversion)
        {
            _conversion = conversion;
        }

        public override void WriteJson(JsonWriter writer, ulong value, JsonSerializer serializer)
        {
            switch (_conversion)
            {
                case NumberConversion.Hex:
                    writer.WriteValue(value == 0UL ? "0x0" : value.ToHexString(true));
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

        public override ulong ReadJson(JsonReader reader, Type objectType, ulong existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return reader.Value is ulong || reader.Value is int
                ? (ulong)reader.Value
                : FromString(reader.Value?.ToString());
        }

        public static ulong FromString(string s)
        {
            if (s is null)
            {
                throw new JsonException("null cannot be assigned to long");
            }

            if (s == "0x0")
            {
                return 0UL;
            }

            if (s.StartsWith("0x0"))
            {
                return ulong.Parse(s.AsSpan(2), NumberStyles.AllowHexSpecifier);
            }

            if (s.StartsWith("0x"))
            {
                Span<char> withZero = new(new char[s.Length - 1]);
                withZero[0] = '0';
                s.AsSpan(2).CopyTo(withZero.Slice(1));
                return ulong.Parse(withZero, NumberStyles.AllowHexSpecifier);
            }

            return ulong.Parse(s, NumberStyles.Integer);
        }
    }
}
