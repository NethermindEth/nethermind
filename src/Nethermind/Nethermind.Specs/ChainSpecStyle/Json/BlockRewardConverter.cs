// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json;

public class BlockRewardConverter : JsonConverter<SortedDictionary<long, UInt256>>
{
    public override void Write(Utf8JsonWriter writer, SortedDictionary<long, UInt256> value,
        JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }

    public override SortedDictionary<long, UInt256> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = new SortedDictionary<long, UInt256>();
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
                    throw new ArgumentException("Cannot deserialize dictionary.");
                }

                var property =
                    UInt256Converter.ReadHex(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
                var key = (long)property;
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new ArgumentException("Cannot deserialize dictionary.");
                }

                var blockReward =
                    UInt256Converter.ReadHex(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
                value.Add(key, blockReward);

                reader.Read();
            }
        }
        else
        {
            throw new ArgumentException("Cannot deserialize dictionary.");
        }

        return value;
    }
}
