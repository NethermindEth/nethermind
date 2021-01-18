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
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class NullableUInt256Converter : JsonConverter<UInt256?>
    {
        private UInt256Converter _uInt256Converter;
        
        public NullableUInt256Converter()
            : this(NumberConversion.Hex)
        {
        }

        public NullableUInt256Converter(NumberConversion conversion)
        {
            _uInt256Converter = new UInt256Converter(conversion);
        }

        public override void WriteJson(JsonWriter writer, UInt256? value, JsonSerializer serializer)
        {
            if (!value.HasValue)
            {
                writer.WriteNull();
                return;
            }
            
            _uInt256Converter.WriteJson(writer, value.Value, serializer);
        }

        public override UInt256? ReadJson(JsonReader reader, Type objectType, UInt256? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }
            
            return _uInt256Converter.ReadJson(reader, objectType, existingValue ?? 0, hasExistingValue, serializer);
        }
    }
}
