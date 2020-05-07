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
// 

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Json
{
    public class JsonConverterSyncingStatus : JsonConverter<SyncingStatus>
    {
        public override SyncingStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var properties = new object[3];
            while (true)
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }
                else if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("StartingSlot"))
                    {
                        properties[0] = JsonSerializer.Deserialize<Slot>(ref reader, options);
                    }
                    else if (reader.ValueTextEquals("CurrentSlot"))
                    {
                        properties[1] = JsonSerializer.Deserialize<Slot>(ref reader, options);
                    }
                    else if (reader.ValueTextEquals("HighestSlot"))
                    {
                        properties[2] = JsonSerializer.Deserialize<Slot>(ref reader, options);
                    }
                }
                else
                {
                    if (!reader.Read())
                    {
                        break;
                    }
                }
            }
            return new SyncingStatus((Slot)properties[0], (Slot)properties[1], (Slot)properties[2]);
        }

        public override void Write(Utf8JsonWriter writer, SyncingStatus value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(nameof(value.StartingSlot));
            JsonSerializer.Serialize(writer, value.StartingSlot, options);
            
            writer.WriteNumber(nameof(value.CurrentSlot), value.CurrentSlot);
            writer.WriteNumber(nameof(value.HighestSlot), value.HighestSlot);
        }
    }
}