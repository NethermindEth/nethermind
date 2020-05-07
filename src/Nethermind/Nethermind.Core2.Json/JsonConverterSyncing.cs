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
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Api;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Json
{
    public class JsonConverterSyncing : JsonConverter<Syncing>
    {
        public override Syncing Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var properties = new object?[2];
            while (true)
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }
                else if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("IsSyncing"))
                    {
                        properties[0] = reader.GetBoolean();
                    }
                    else if (reader.ValueTextEquals("SyncStatus"))
                    {
                        properties[1] = JsonSerializer.Deserialize<SyncingStatus?>(ref reader, options);
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
            return new Syncing((bool)properties[0], properties[1] as SyncingStatus);
        }

        public override void Write(Utf8JsonWriter writer, Syncing value, JsonSerializerOptions options)
        {
            writer.WriteBoolean(nameof(value.IsSyncing), value.IsSyncing);
            writer.WritePropertyName(nameof(value.SyncStatus));
            JsonSerializer.Serialize(writer, value.SyncStatus, options);
        }
    }
}