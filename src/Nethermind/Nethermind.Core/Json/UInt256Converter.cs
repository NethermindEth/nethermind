/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Globalization;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json;

namespace Nethermind.Core.Json
{
    public class UInt256Converter : JsonConverter<UInt256>
    {
        private readonly NumberConversion _conversion;

        public UInt256Converter()
            : this(NumberConversion.Hex)
        {
        }

        public UInt256Converter(NumberConversion conversion)
        {
            _conversion = conversion;
        }

        public override void WriteJson(JsonWriter writer, UInt256 value, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (value.IsZero)
            {
                writer.WriteValue("0x0");
                return;
            }

            NumberConversion usedConversion = _conversion == NumberConversion.Decimal
                ? value < int.MaxValue ? NumberConversion.Decimal : NumberConversion.Hex
                : _conversion;

            switch (usedConversion)
            {
                case NumberConversion.PaddedHex:
                    writer.WriteValue(string.Concat("0x", value.ToString("x64").TrimStart('0')));
                    break;
                case NumberConversion.Hex:
                    writer.WriteValue(string.Concat("0x", value.ToString("x").TrimStart('0')));
                    break;
                case NumberConversion.Decimal:
                    writer.WriteValue((int) value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public override UInt256 ReadJson(JsonReader reader, Type objectType, UInt256 existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.Value is long || reader.Value is int)
            {
                return new UInt256((long) reader.Value);
            }

            string s = (string) reader.Value;
            if (s == "0x0")
            {
                return UInt256.Zero;
            }

            if (s.StartsWith("0x0"))
            {
                return UInt256.Parse(s.AsSpan(2), NumberStyles.AllowHexSpecifier);
            }

            if (s.StartsWith("0x"))
            {
                Span<char> withZero = new Span<char>(new char[s.Length - 1]);
                withZero[0] = '0';
                s.AsSpan(2).CopyTo(withZero.Slice(1));
                return UInt256.Parse(withZero, NumberStyles.AllowHexSpecifier);
            }

            try
            {
                return UInt256.Parse(s, NumberStyles.Integer);
            }
            catch (Exception)
            {
                return UInt256.Parse(s, NumberStyles.AllowHexSpecifier);
            }
        }
    }
}