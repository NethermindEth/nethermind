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
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc
{
    public class TupleListConverter<U, V> : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return typeof(Tuple<U, V>) == objectType;
        }

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            var jArray = Newtonsoft.Json.Linq.JArray.Load(reader);
            var target = new List<Tuple<U, V>>();

            foreach (var childJArray in jArray.Children<Newtonsoft.Json.Linq.JArray>())
            {
                var tuple = new Tuple<U, V>(
                    serializer.Deserialize<U>((new JsonTextReader(new StringReader(childJArray[0].ToString())))),
                    childJArray[1].ToObject<V>()
                );
                target.Add(tuple);
            }

            return target;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, value);
        }
    }
}
