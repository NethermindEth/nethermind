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
using System.Numerics;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json;

namespace Nethermind.Core.Json
{
    public class IdConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            switch (value)
            {
                case int typedValue:
                    writer.WriteValue(typedValue);
                    break;
                case long typedValue:
                    writer.WriteValue(typedValue);
                    break;
                case BigInteger typedValue:
                    writer.WriteValue(typedValue);
                    break;
                case string typedValue:
                    writer.WriteValue(typedValue);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Integer:
                    return reader.Value;
                case JsonToken.String:
                    return reader.Value as string;
                default:
                    throw new NotSupportedException($"{reader.TokenType}");
            }
        }

        public override bool CanConvert(Type objectType)
        {
            return true;
        }
    }
}