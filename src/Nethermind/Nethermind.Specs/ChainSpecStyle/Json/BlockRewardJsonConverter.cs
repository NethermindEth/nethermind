// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;
using System.Buffers;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class BlockRewardJsonConverter : JsonConverter<ChainSpecJson.BlockRewardJson>
    {
        public override void Write(Utf8JsonWriter writer, ChainSpecJson.BlockRewardJson value, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override ChainSpecJson.BlockRewardJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = new ChainSpecJson.BlockRewardJson();
            if (reader.TokenType == JsonTokenType.String)
            {
                var blockReward = JsonSerializer.Deserialize<UInt256>(ref reader, options);
                value.Add(0, blockReward);
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                value.Add(0, new UInt256(reader.GetUInt64()));
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
                    var property = UInt256Converter.Read(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
                    var key = (long)property;
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new ArgumentException("Cannot deserialize BlockReward.");
                    }

                    var blockReward = UInt256Converter.Read(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
                    value.Add(key, blockReward);

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
