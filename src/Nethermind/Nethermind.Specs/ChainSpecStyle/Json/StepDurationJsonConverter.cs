// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
