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
using System.Collections.Generic;
using Newtonsoft.Json;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTraceAddressConverter : JsonConverter<int[]>
    {
        public override void WriteJson(JsonWriter writer, int[] value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteStartArray();
                foreach (int i in value)
                {
                    writer.WriteValue(i);
                }

                writer.WriteEndArray();
            }
        }

        public override int[] ReadJson(JsonReader reader, Type objectType, int[] existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            List<int> result = new();
            int? pathPart;

            do
            {
                pathPart = reader.ReadAsInt32();
                if (pathPart.HasValue)
                {
                    result.Add(pathPart.Value);
                }
            } while (pathPart != null);
            
            return result.ToArray();
        }
    }
}
