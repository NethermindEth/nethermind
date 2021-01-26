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
using Nethermind.JsonRpc.Modules.Trace;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class SyncingResultConverter : JsonConverter<SyncingResult>
    {
        public override void WriteJson(JsonWriter writer, SyncingResult value, JsonSerializer serializer)
        {
            if (!value.IsSyncing)
            {
                writer.WriteValue(false);
                return;
            }

            writer.WriteStartObject();
            writer.WriteProperty("startingBlock", value.StartingBlock, serializer);
            writer.WriteProperty("currentBlock", value.CurrentBlock, serializer);
            writer.WriteProperty("highestBlock", value.HighestBlock, serializer);
            writer.WriteEndObject();
        }

        public override SyncingResult ReadJson(JsonReader reader, Type objectType, SyncingResult existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
