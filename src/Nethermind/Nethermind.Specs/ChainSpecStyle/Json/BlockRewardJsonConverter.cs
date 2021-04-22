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
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class BlockRewardJsonConverter : JsonConverter<ChainSpecJson.BlockRewardJson>
    {
        public override void WriteJson(JsonWriter writer, ChainSpecJson.BlockRewardJson value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override ChainSpecJson.BlockRewardJson ReadJson(JsonReader reader, Type objectType, ChainSpecJson.BlockRewardJson existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            existingValue ??= new ChainSpecJson.BlockRewardJson();
            if (reader.TokenType == JsonToken.String || reader.TokenType == JsonToken.Integer)
            {
                var blockReward = serializer.Deserialize<UInt256>(reader);
                existingValue.Add(0, blockReward);
            }
            else
            {
                var blockRewards = serializer.Deserialize<Dictionary<string, UInt256>>(reader);
                foreach (var blockReward in blockRewards ?? throw new ArgumentException("Cannot deserialize BlockReward."))
                {
                    existingValue.Add(LongConverter.FromString(blockReward.Key), blockReward.Value);
                }
            }

            return existingValue;
        }
    }
}
