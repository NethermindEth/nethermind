// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class StepDurationJsonConverter : JsonConverter<ChainSpecJson.AuraEngineParamsJson.StepDurationJson>
    {
        public override void Write(Utf8JsonWriter writer, ChainSpecJson.AuraEngineParamsJson.StepDurationJson value, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override ChainSpecJson.AuraEngineParamsJson.StepDurationJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = new ChainSpecJson.AuraEngineParamsJson.StepDurationJson();
            if (reader.TokenType == JsonTokenType.String)
            {
                value.Add(0, long.Parse(reader.GetString()));
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                value.Add(0, reader.GetInt64());
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                reader.Read();
                while (reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new ArgumentException("Cannot deserialize BlockReward.");
                    }
                    var key = reader.GetInt64();
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new ArgumentException("Cannot deserialize BlockReward.");
                    }

                    value.Add(key, reader.GetInt64());
                    reader.Read();
                }
            }
            else
            {
                throw new ArgumentException("Cannot deserialize BlockReward.");
            }

            return value;
        }
    }
}
