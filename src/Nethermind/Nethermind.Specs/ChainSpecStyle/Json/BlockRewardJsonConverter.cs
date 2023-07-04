// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
