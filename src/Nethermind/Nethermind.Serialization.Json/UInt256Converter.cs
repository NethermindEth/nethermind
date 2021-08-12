//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
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

        public override void WriteJson(JsonWriter writer, UInt256 value, JsonSerializer serializer)
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
                case NumberConversion.Hex:
                    writer.WriteValue(value.ToHexString(true));
                    break;
                case NumberConversion.Decimal:
                    writer.WriteRawValue(value.ToString());
                    break;
                default:
                    throw new NotSupportedException($"{usedConversion} format is not supported for {nameof(UInt256)}");
            }
        }

        public override UInt256 ReadJson(JsonReader reader, Type objectType, UInt256 existingValue, bool hasExistingValue, JsonSerializer serializer) => 
            ReaderJson(reader);

        public static UInt256 ReaderJson(JsonReader reader)
        {
            if (reader.Value is long || reader.Value is int)
            {
                return (UInt256) (long) reader.Value;
            }

            string s = reader.Value?.ToString();
            if (s == null)
            {
                throw new JsonException($"{nameof(UInt256)} cannot be deserialized from null");
            }

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
                Span<char> withZero = new(new char[s.Length - 1]);
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
                return UInt256.Parse(s, NumberStyles.HexNumber);
            }
        }
    }
}
