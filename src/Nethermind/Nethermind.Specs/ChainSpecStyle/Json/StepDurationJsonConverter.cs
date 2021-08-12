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
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class StepDurationJsonConverter : JsonConverter<ChainSpecJson.AuraEngineParamsJson.StepDurationJson>
    {
        public override void WriteJson(JsonWriter writer, ChainSpecJson.AuraEngineParamsJson.StepDurationJson value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }

        public override ChainSpecJson.AuraEngineParamsJson.StepDurationJson ReadJson(JsonReader reader, Type objectType, ChainSpecJson.AuraEngineParamsJson.StepDurationJson existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            existingValue ??= new ChainSpecJson.AuraEngineParamsJson.StepDurationJson();
            if (reader.TokenType == JsonToken.String || reader.TokenType == JsonToken.Integer)
            {
                var stepDuration = serializer.Deserialize<long>(reader);
                existingValue.Add(0, stepDuration);
            }
            else
            {
                var stepDurations = serializer.Deserialize<Dictionary<string, long>>(reader);
                foreach (var stepDuration in stepDurations ?? throw new ArgumentException("Cannot deserialize StepDuration."))
                {
                    existingValue.Add(LongConverter.FromString(stepDuration.Key), stepDuration.Value);
                }
            }

            return existingValue;
        }
    }
}
