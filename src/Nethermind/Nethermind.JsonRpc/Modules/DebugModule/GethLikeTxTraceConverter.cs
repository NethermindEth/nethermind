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
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class GethLikeTxTraceConverter : JsonConverter<GethLikeTxTrace>
    {
        public override void WriteJson(JsonWriter writer, GethLikeTxTrace value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();

            writer.WriteProperty("gas", value.Gas, serializer);
            writer.WriteProperty("failed", value.Failed);
            writer.WriteProperty("returnValue", value.ReturnValue, serializer);

            writer.WritePropertyName("structLogs");
            WriteEntries(writer, value.Entries, serializer);

            writer.WriteEndObject();
        }

        private static void WriteEntries(JsonWriter writer, List<GethTxTraceEntry> entries, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            foreach (GethTxTraceEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteProperty("pc", entry.Pc);
                writer.WriteProperty("op", entry.Operation);
                writer.WriteProperty("gas", entry.Gas);
                writer.WriteProperty("gasCost", entry.GasCost);
                writer.WriteProperty("depth", entry.Depth);
                writer.WriteProperty("error", entry.Error);
                writer.WritePropertyName("stack");
                writer.WriteStartArray();
                foreach (string stackItem in entry.Stack)
                {
                    writer.WriteValue(stackItem);    
                }
                
                writer.WriteEndArray();
                
                writer.WritePropertyName("memory");
                writer.WriteStartArray();
                foreach (string memory in entry.Memory)
                {
                    writer.WriteValue(memory);    
                }
                writer.WriteEndArray();
                
                writer.WritePropertyName("storage");
                writer.WriteStartObject();
                foreach ((string storageIndex, string storageValue) in entry.SortedStorage)
                {
                    writer.WriteProperty(storageIndex, storageValue);
                }
                
                writer.WriteEndObject();
                
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        public override GethLikeTxTrace ReadJson(JsonReader reader, Type objectType, GethLikeTxTrace existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
