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
using Nethermind.Evm.Tracing.ParityStyle;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTraceResultConverter : JsonConverter<ParityTraceResult>
    {
        public override void WriteJson(JsonWriter writer, ParityTraceResult value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            if (value.Address != null)
            {
                writer.WriteProperty("address", value.Address, serializer);    
                writer.WriteProperty("code", value.Code, serializer);
            }
            
            writer.WriteProperty("gasUsed", string.Concat("0x", value.GasUsed.ToString("x")));
            
            if(value.Address == null)
            {
                writer.WriteProperty("output", value.Output, serializer);    
            }
            
            writer.WriteEndObject();
        }

        public override ParityTraceResult ReadJson(JsonReader reader, Type objectType, ParityTraceResult existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
