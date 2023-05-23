// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Numerics;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class BigIntegerConverter : JsonConverter<BigInteger>
    {
        private readonly NumberConversion _conversion;

        public BigIntegerConverter()
            : this(NumberConversion.Hex)
        {
        }

        public BigIntegerConverter(NumberConversion conversion)
        {
            _conversion = conversion;
        }

        public override void WriteJson(JsonWriter writer, BigInteger value, JsonSerializer serializer)
        {
            if (value.IsZero)
            {
                writer.WriteValue("0x0");
                return;
            }

            switch (_conversion)
            {
                case NumberConversion.Hex:
                    writer.WriteValue(string.Concat("0x", value.ToByteArray(false, true).ToHexString()));
                    break;
                case NumberConversion.Decimal:
                    writer.WriteValue(value.ToString());
                    break;
                case NumberConversion.Raw:
                    writer.WriteValue(value);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        public override BigInteger ReadJson(JsonReader reader, Type objectType, BigInteger existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.Value is long || reader.Value is int)
            {
                return (long)reader.Value;
            }

            string s = reader.Value?.ToString();
            if (s == "0x0")
            {
                return BigInteger.Zero;
            }

            bool isHex = false;
            Span<char> withZero = null;
            if (s.StartsWith("0x0"))
            {
                withZero = s.AsSpan(2).ToArray();
                isHex = true;
            }
            else if (s.StartsWith("0x"))
            {
                withZero = new Span<char>(new char[s.Length - 1]);
                withZero[0] = '0';
                s.AsSpan(2).CopyTo(withZero.Slice(1));
                isHex = true;
            }

            if (isHex)
            {
                // withZero.Reverse();
                return BigInteger.Parse(withZero, NumberStyles.AllowHexSpecifier);
            }

            return BigInteger.Parse(s, NumberStyles.Integer);
        }
    }
}
