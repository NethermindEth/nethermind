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
using System.Numerics;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class NullableBigIntegerConverter : JsonConverter<BigInteger?>
    {
        private BigIntegerConverter _bigIntegerConverter;
        
        public NullableBigIntegerConverter()
            : this(NumberConversion.Hex)
        {
        }

        public NullableBigIntegerConverter(NumberConversion conversion)
        {
            _bigIntegerConverter = new BigIntegerConverter(conversion);
        }

        public override void WriteJson(JsonWriter writer, BigInteger? value, JsonSerializer serializer)
        {
            _bigIntegerConverter.WriteJson(writer, value.Value, serializer);
        }

        public override BigInteger? ReadJson(JsonReader reader, Type objectType, BigInteger? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }
            
            return _bigIntegerConverter.ReadJson(reader, objectType, existingValue ?? 0, hasExistingValue, serializer);
        }
    }
}
