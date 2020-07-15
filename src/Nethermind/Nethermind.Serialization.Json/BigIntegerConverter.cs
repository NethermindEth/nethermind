//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

        public override BigInteger ReadJson(JsonReader reader, Type objectType, BigInteger existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.Value is long || reader.Value is int)
            {
                return (long) reader.Value;
            }

            string s = (string) reader.Value;
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