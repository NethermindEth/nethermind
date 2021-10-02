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
// 

using System;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class NullableULongConverter : JsonConverter<ulong?>
    {
        private readonly ULongConverter _ulongConverter;

        public NullableULongConverter()
            : this(NumberConversion.Hex)
        {
        }

        public NullableULongConverter(NumberConversion conversion)
        {
            _ulongConverter = new ULongConverter(conversion);
        }

        public override void WriteJson(JsonWriter writer, ulong? value, JsonSerializer serializer)
        {
            if (!value.HasValue)
            {
                writer.WriteNull();
                return;
            }

            _ulongConverter.WriteJson(writer, value.Value, serializer);
        }

        public override ulong? ReadJson(JsonReader reader, Type objectType, ulong? existingValue, bool hasExistingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            return _ulongConverter.ReadJson(reader, objectType, existingValue ?? 0, hasExistingValue, serializer);
        }
    }
}
